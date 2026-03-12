using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Skia;
using SkiaSharp;
using Thio_Universal_Agent.AI_API;

namespace Thio_Universal_Agent.Logic;

/// <summary>
/// Locates UI elements on screen by iteratively prompting an AI model
/// with grid-overlaid screenshots and refining coordinates through zoom.
/// </summary>
public sealed partial class CoordinatePrompter(IAiProvider aiProvider, IConfiguration configuration)
{
    private readonly AiRequestOptions? _coordinateRequestOptions =
        int.TryParse(configuration["Gemini:CoordinateMaxOutputTokens"], out var maxTokens)
            ? new AiRequestOptions(MaxOutputTokens: maxTokens)
            : null;

    private const int RulerOffset = 100;
    private const int LabelBuffer = 60;
    private const int DefaultDivisions = 10;
    private const double DefaultConfidencePixels = 15.0;
    private const int MaxZoomIterations = 10;
    private const double DefaultAIEstimatePrecision = 0.3; // Assume AI is accurate within ~0.3 of a grid cell

    private int _divisions = DefaultDivisions;

    // Commands / codes that can be used by the AI finding the coordinates
    public sealed record CoordResponseCode
    {
        private static List<string> _allStrings = [];

        /// <summary>The string code to be used by the agent in the response</summary>
        public string Code { get; }
        /// <summary>If the code does not require any additional text or info following it.</summary>
        public bool IsStandalone { get; }

        // Private constructor
        private CoordResponseCode(string code, bool isStandalone)
        {
            Code = code;
            IsStandalone = isStandalone;

            // Update the internal list so the public method is accurate
            _allStrings.Add(code);
        }

        // Private methods
        private static CoordResponseCode? StringToCode (string code)
        {
            return code.ToUpper() switch
            {
                "COORDS" => COORDS,
                "CANNOT_FIND" => CANNOT_FIND,
                "UNSURE" => UNSURE,
                _ => null
            };
        }

        // Instances
        public static readonly CoordResponseCode COORDS = new(code: "COORDS", isStandalone: false);
        public static readonly CoordResponseCode CANNOT_FIND = new(code: "CANNOT_FIND", isStandalone: true);
        public static readonly CoordResponseCode UNSURE = new(code: "UNSURE", isStandalone: true);
        //public static readonly CoordResponseCode UNKNOWN = new(code: "", isStandalone: true); // Default to COORDS if not sure

        // ---------- Public Methods ----------
        public static List<string> AllCodeStrings { get { return _allStrings; } }

        // Input a list of strings and get back any matched commands
        public static List<CoordResponseCode> GetFromStrings(List<string> codes)
        {
            List<CoordResponseCode> result = new();
            foreach (string code in codes)
            {
                CoordResponseCode? c = StringToCode(code);
                if (c is not null)
                    result.Add(c);
            }
            return result;
        }

        // Overload allowing input of a single string. Still returns a list, but if it's not found it will be empty.
        public static List<CoordResponseCode> GetFromStrings (string code)
        {
            return GetFromStrings([code]);
        }

        // Allow the class type to be used as a string where it returns the Code property
        public static implicit operator string(CoordResponseCode c) => c.Code;
        public override string ToString() => Code;
    }

    /// <summary>Tracks a rectangular crop window in original image pixel coordinates.</summary>
    private record ViewRegion(double X, double Y, double Width, double Height);

    /// <summary>A parsed grid coordinate returned by the LLM.</summary>
    private record GridCoordinate(double X, double Y);

    /// <summary>Diagnostic data for a single iteration of the coordinate prompting loop.</summary>
    public record CoordinateStep(
        int StepNumber,
        byte[] GridImage,
        string AiResponseText,
        double? ParsedX,
        double? ParsedY,
        byte[] AnnotatedImage);

    /// <summary>
    /// Locates an item on screen by iteratively prompting the AI with grid-overlaid
    /// versions of the screenshot, returning the absolute screen pixel coordinates.
    /// </summary>
    public async Task<(double X, double Y)> GetCoordinatesForItemAsync(
        byte[] screenshotBytes,
        string itemToIdentify,
        Func<CoordinateStep, Task>? onStepCompleted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screenshotBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemToIdentify);

        using IImage source = LoadImage(screenshotBytes);
        ViewRegion view = CreateFullView(source);
        int imageWidth = (int)source.Width;
        int imageHeight = (int)source.Height;
        int stepNumber = 0;

        int divisions = _divisions; // Maybe later add a config for this, or a way to set the variable

        byte[] gridImage = CreateGridOverlayImage(source, view, divisions, divisions);
        string prompt = MakeCoordinatePrompt(itemToIdentify);

        // Start the conversation with the prompt + initial grid image
        var conversation = new AiConversation();

        // -------------- LOCAL FUNCTION - To be used upon failure --------------
        GridCoordinate RetryWithRecoveryInstructions(ParseFailReason? failReason, string previousResponse)
        {
            if (failReason is null) // failReason should never be null if coordinates are null, but just in case, fallback to NoCommandFound
                failReason = new ParseFailReason(ParseFailReasons.NoCommandFound);

            AiResponse retryResponse = aiProvider.ContinueConversationAsync(
                    conversation: conversation,
                    prompt: "ERROR: " + failReason.RecoveryInstructions,
                    imageBytes: gridImage,
                    mimeType: "image/png",
                    cancellationToken: cancellationToken,
                    options: _coordinateRequestOptions)
                .GetAwaiter().GetResult(); // Using GetAwaiter().GetResult() here to call the async method synchronously within this local function

            (GridCoordinate? retryCoordinate, ParseFailReason? failReason2, CoordResponseCode responseCode) = ParseCoordinateResponse(retryResponse.Text);
            if (retryCoordinate is null)
            {
                // If it still fails after recovery attempt, we have no choice but to throw
                throw new InvalidOperationException($"AI failed to provide valid coordinates after recovery attempt. Last parsing failure reason: {failReason?.Details}." +
                    $"\nOriginal AI response was: '{previousResponse}'" +
                    $"\nRecovery Response was: '{retryResponse.Text}'"
                    );
            }

            stepNumber++;
            return retryCoordinate;

        }
        // -------------------------------------------------------------------------    

        AiResponse response = await aiProvider.ContinueConversationAsync(
            conversation, prompt, gridImage, "image/png", cancellationToken, _coordinateRequestOptions)
            .ConfigureAwait(false);

        (GridCoordinate? coordinate, ParseFailReason? failReason, CoordResponseCode responseCode) = ParseCoordinateResponse(response.Text);

        // If a special response code was given, we need to alert the outer AI
        if (responseCode != CoordResponseCode.COORDS)
        {
            if (responseCode == CoordResponseCode.CANNOT_FIND)
                throw new InvalidOperationException("The AI could not find the requested item. Please give an alternative description.");
            else if (responseCode == CoordResponseCode.UNSURE)
                throw new InvalidOperationException("The AI was unsure about the location of the item, possibly due to multiple similar items on screen. Please give a more specific description to help it differentiate.");
            else
                throw new InvalidOperationException($"Received unexpected response code from AI: {responseCode}. Please try a different item description.");
        }

        // If it failed to parse, instead of throwing, give the recovery instructions to the agent and have it try again once
        if (coordinate is null)
            coordinate = RetryWithRecoveryInstructions(failReason, response.Text);
        else if (!CheckCoordinatesWithinBounds(coordinate))
            coordinate = RetryWithRecoveryInstructions(new ParseFailReason(ParseFailReasons.InvalidCoordinates), response.Text);

        stepNumber++;

        if (onStepCompleted is not null)
        {
            byte[] annotated = CreateAnnotatedImage(gridImage, coordinate, imageWidth, imageHeight, divisions, divisions);
            await onStepCompleted(new CoordinateStep(
                stepNumber, gridImage, response.Text, coordinate.X, coordinate.Y, annotated))
                .ConfigureAwait(false);
        }

        // Iteratively zoom in until each grid cell is within the confidence threshold
        for (int i = 0; i < MaxZoomIterations && ShouldContinueZooming(view, divisions, divisions); i++)
        {
            view = CalculateZoomRegion(view, coordinate, divisions, divisions);
            byte[] zoomedImage = CreateGridOverlayImage(source, view);

            // Send just the zoomed image; the AI continues from the same conversation
            response = await aiProvider.ContinueConversationAsync(
                conversation, zoomedImage, "image/png", cancellationToken, _coordinateRequestOptions)
                .ConfigureAwait(false);

            stepNumber++;

            if (!response.Success)
            {
                if (onStepCompleted is not null)
                    await onStepCompleted(new CoordinateStep(
                        stepNumber, zoomedImage, response.ErrorMessage ?? "(failed)", null, null, zoomedImage))
                        .ConfigureAwait(false);
                break;
            }

            // Comprehensive attempt to parse the coordinates
            (GridCoordinate? parsed, failReason, responseCode) = ParseCoordinateResponse(response.Text);
            if (parsed is null)
                parsed = RetryWithRecoveryInstructions(failReason, response.Text);
            else if (!CheckCoordinatesWithinBounds(coordinate))
                coordinate = RetryWithRecoveryInstructions(new ParseFailReason(ParseFailReasons.InvalidCoordinates), response.Text);

            if (onStepCompleted is not null)
            {
                byte[] annotated = parsed is not null
                    ? CreateAnnotatedImage(zoomedImage, parsed, imageWidth, imageHeight, divisions, divisions)
                    : zoomedImage;
                await onStepCompleted(new CoordinateStep(
                    stepNumber, zoomedImage, response.Text, parsed?.X, parsed?.Y, annotated))
                    .ConfigureAwait(false);
            }

            if (parsed is null)
                break;
            else
                coordinate = parsed;
        }

        return CalculateScreenCoordinates(view, coordinate, divisions, divisions);
    }




    /// <summary>Builds the prompt text that asks the LLM to identify grid coordinates.</summary>
    private static string MakeCoordinatePrompt(string itemToIdentify, int divisions = DefaultDivisions)
    {
        return $"Return the X, Y grid coordinates closest to the item with the following description in the given screenshot: {itemToIdentify}.\n\n"
             + $"\nIf you can identify the item, output, respond with {CoordResponseCode.COORDS} then the comma sparated X,Y coordinates. Each coordinate must be between 0 and {divisions}, and contain a single decimal."
             + $"\n    Example Response:  \"{CoordResponseCode.COORDS} 3.2, 7.5\""
             + $"\n\nALTERNATIVE RETURN CODES:"
             + $"\n\nIf you cannot locate the described item at all, respond only with: {CoordResponseCode.CANNOT_FIND}"
             + $"\nIf the described item is ambiguous or there are multiple items of equal likelihood, respond only with: {CoordResponseCode.UNSURE}";
    }



    private enum ParseFailReasons
    {
        /// <summary>The response was an empty string.</summary>
        EmptyResponse,

        /// <summary>One of the possible valid response codes was used, but not in the correct structure.</summary>
        CorrectCommand_WrongStructure,

        /// <summary>Multiple different response codes were used. Only one is allowed.</summary>
        MultipleCommandsUsed,

        /// <summary>None of the valid response codes were found in the response.</summary>
        NoCommandFound,

        /// <summary>Correct command was used but the coordinates are outside the bounds of the grid</summary>
        InvalidCoordinates
    }

    private class ParseFailReason
    {
        // Friendly description of the error for humans
        public string Details { get; }
        // Description of error and instructions for how to fix, intended for the agent
        public string RecoveryInstructions { get; }

        public ParseFailReason(ParseFailReasons reason, string? details = null, string? recoveryInstructions = null)
        {
            // Use default details if none are provided
            if (details == null)
            {
                switch (reason)
                {
                    case ParseFailReasons.EmptyResponse:
                        details = "Response was entirely empty.";
                        break;
                    case ParseFailReasons.CorrectCommand_WrongStructure:
                        details = "Valid command was used with incorrect structure.";
                        break;
                    case ParseFailReasons.MultipleCommandsUsed:
                        details = "Multiple different response codes were used.";
                        break;
                    case ParseFailReasons.NoCommandFound:
                        details = "No valid commands found in response.";
                        break;
                    case ParseFailReasons.InvalidCoordinates:
                        details = "Coordinates were found but are outside the bounds of the grid.";
                        break;
                    default:
                        details = "An unknown parsing error occurred.";
                        break;
                }
            } 

            // Use default recovery instructions if none are provided
            if (recoveryInstructions == null)
            {
                switch (reason)
                {
                    case ParseFailReasons.EmptyResponse:
                        recoveryInstructions = "Your response appears to have been completely empty or was not retrieved correctly. Please try again and provide a valid response to the same task.";
                        break;
                    case ParseFailReasons.CorrectCommand_WrongStructure:
                        recoveryInstructions = "A correct command code was found, but failed to parse. Please ensure the response command code is used in the correct structure.";
                        break;
                    case ParseFailReasons.MultipleCommandsUsed:
                        recoveryInstructions = "Please use only one command code in the response.";
                        break;
                    case ParseFailReasons.NoCommandFound:
                        recoveryInstructions = "None of the valid response codes were found in the response. Must use exactly one of the following response codes: " + string.Join(", ", CoordResponseCode.AllCodeStrings);
                        break;
                    case ParseFailReasons.InvalidCoordinates:
                        recoveryInstructions = "The coordinates you provided are outside the bounds of the grid. Please provide coordinates where both X and Y are between 0 and " + DefaultDivisions + ", with a single decimal place.";
                        break;
                    default:
                        recoveryInstructions = "Please review the parsing error details and adjust your response accordingly.";
                        break;
                }
            }

            Details = details;
            RecoveryInstructions = recoveryInstructions;
        }
    }

    /// <summary>Parses a comma-separated "X, Y" coordinate string from the LLM response.</summary>
    private static (GridCoordinate? coordinates, ParseFailReason? failReason, CoordResponseCode responseCode) ParseCoordinateResponse(string response)
    {
        // First check for special response codes that need to be handled
        if (response.Contains(CoordResponseCode.CANNOT_FIND.Code, StringComparison.OrdinalIgnoreCase))
            return (coordinates: null, failReason: null, responseCode: CoordResponseCode.CANNOT_FIND);

        if (response.Contains(CoordResponseCode.UNSURE.Code, StringComparison.OrdinalIgnoreCase))
            return (coordinates: null, failReason: null, responseCode: CoordResponseCode.UNSURE);

        // -----------------------------------------------------------

        if (string.IsNullOrWhiteSpace(response))
            return (coordinates: null, failReason: new ParseFailReason(ParseFailReasons.EmptyResponse), responseCode: CoordResponseCode.COORDS);

        List<string> foundCommandStrings = CoordResponseCode.AllCodeStrings.Where(cmd => response.Contains(cmd, StringComparison.OrdinalIgnoreCase)).ToList();
        List<CoordResponseCode> foundCommands = CoordResponseCode.GetFromStrings(foundCommandStrings);

        if (foundCommandStrings.Count == 0)
        {
            // Check if it just returned the coordinates without a command code
            string[] possibleCoordParts = response.Trim().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (ParseValidCoords(possibleCoordParts) is GridCoordinate foundValidCoords)
            {
                return (coordinates: foundValidCoords, failReason: null, responseCode: CoordResponseCode.COORDS); // SUCCESS
            }
            else
            {
                // Check if there was a possible incorrect command used by looking for strings with all caps
                // Use regex to find any words that consists of either all capital letters and underscores
                // This is a mistake I anticipate, so we can give it a more specific error message to help it recover
                Regex regex = AllCapsCommandRegex();
                MatchCollection matches = regex.Matches(response);
                if (matches.Count > 0)
                {
                    string possibleCommand = matches[0].Value;
                    return (
                        coordinates: null,
                        failReason: new ParseFailReason(ParseFailReasons.NoCommandFound,
                            details: $"No valid code found. Found a possible invalid code: '{possibleCommand}'",
                            recoveryInstructions: $"No valid response code found. Found a possible invalid / non-existent code: '{possibleCommand}'" +
                                $"\nPlease use exactly one of the following valid codes in your response: {string.Join(", ", CoordResponseCode.AllCodeStrings)}." +
                                $"\nThe command should be used exactly as specified, without additional characters attached."
                        ),
                        responseCode: CoordResponseCode.COORDS
                    );
                }

                return (coordinates: null, failReason: new ParseFailReason(ParseFailReasons.NoCommandFound), responseCode: CoordResponseCode.COORDS);
            }
        }
        else if (foundCommands.Count > 1)
        {
            return (
                coordinates: null,
                failReason: new ParseFailReason(ParseFailReasons.MultipleCommandsUsed,
                    details: null,
                    recoveryInstructions: "Multiple different response codes were found in the response: " + string.Join(", ", foundCommandStrings)
                        + "\nYour response must use exactly one response code."
                    ),
                responseCode: CoordResponseCode.COORDS
                );
        }


        // Reaching this point, a single valid command was found somewhere. We don't know if the structure is correct yet though.
        CoordResponseCode foundCommand = foundCommands.First();

        // Split the command from the rest of the output. We'll add some extra processing to clean it up in case it's not perfect,
        //      like if it adds a colon after COMMAND: even though it's not supposed to.
        string[] parts = response.Split(foundCommand.Code, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Splitting removes the string of the command. If there's nothing left, it means it was used standalone, so make sure it's a standalone code
        if (parts.Length == 0 && !foundCommand.IsStandalone) {
            return (
                coordinates: null,
                failReason: new ParseFailReason(ParseFailReasons.CorrectCommand_WrongStructure
                    , details: $"The command '{foundCommand}' was found but no additional text was found after it. Expected structure is '{foundCommand} X, Y'"
                    ),
                responseCode: CoordResponseCode.COORDS
                );
        }
        else if (parts.Length > 0 && foundCommand.IsStandalone)
        {
            // Don't do anything here. Might add special handling later, but we'll just ignore any details the AI sends along with a standalone code
        }

        // At this point we know it's not a standalone code and text was sent along with it. Now we parse.
        if (foundCommand == CoordResponseCode.COORDS)
        {
            string[] coordParts = response.Trim().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            // This will check if there's anything immediately wrong, like too many parts, or if any of the parts are more than numbers
            // If it's all good, return the coordinates right away.
            if (ParseValidCoords(coordParts) is GridCoordinate validCoords)
            {
                return (coordinates: validCoords, failReason: null, responseCode: CoordResponseCode.COORDS); // SUCCESS
            }
            // If anything went wrong we'll further clean and parse
            else
            {
                // Trim any colons, quotes, etc, before splitting. Like if it added them right after the command
                string infoString = parts[0].Trim().TrimStart(':').Trim('"').Trim('\'').Trim();

                // Check if it added labels to the coordinates or split on the wrong thing
                string[] possibleSplitters = [";", ",", "\n"];
                foreach (string splitter in possibleSplitters)
                {
                    GridCoordinate? result = SplitAndCheck(infoString, splitter);
                    if (result is not null)
                        return (coordinates: result, failReason: null, responseCode: CoordResponseCode.COORDS); // SUCCESS
                }

                return (
                    coordinates: null,
                    failReason: new ParseFailReason(ParseFailReasons.CorrectCommand_WrongStructure,
                        details: $"The command '{foundCommand}' was found and some text was found after it, but it failed to parse as coordinates. Expected structure is '{foundCommand} X, Y' where X and Y are numbers. Received text after command was: '{parts[0]}'"
                        ),
                    responseCode: CoordResponseCode.COORDS
                    );
            }
        }

        // No success. 
        string recoveryInstructions = $"The command '{foundCommand}' was found but the rest of the response did not match expected format for that command. " +
            $"Please ensure your response follows the expected structure for the command you are using. Expected structure for '{foundCommand}' is: ";

        if (foundCommand.IsStandalone)
            recoveryInstructions += $"just the command alone with no additional text.";
        else
            recoveryInstructions += $"'{foundCommand} X, Y' where X and Y are numbers between 0 and {DefaultDivisions} with a single decimal place, separated by a comma.";

        return (
            coordinates: null,
            failReason: new ParseFailReason(ParseFailReasons.CorrectCommand_WrongStructure,
                details: null,
                recoveryInstructions: recoveryInstructions
                ),
            responseCode: CoordResponseCode.COORDS
            );


        // ------------------- LOCAL FUNCTIONS ----------------------------
        // Run all the checks with a possible incorrect splitting char
        static GridCoordinate? SplitAndCheck(string infoString, string splitOn)
        {
            string[] cleanedParts = infoString.Split(splitOn, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            // Return if it's valid now, otherwise we'll do more checks
            if (ParseValidCoords(cleanedParts) is GridCoordinate validCoords2)
                return validCoords2; // SUCCESS

            // More aggressive checking. Remove possible prefixes like "x:", "X :", "y=", "Y = ", etc.
            string[] prefixStrippedParts = cleanedParts
                .Select(p => CoordPrefixRegex().Replace(p.Trim(), string.Empty).Trim())
                .ToArray();

            if (ParseValidCoords(prefixStrippedParts) is GridCoordinate validCoordsFromPrefixStrip)
                return validCoordsFromPrefixStrip; // SUCCESS

            // Filter for parts that are only numbers, check if there's exactly two, if so use those
            string[] numberParts = cleanedParts.Where(p => double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out _)).ToArray();
            if (ParseValidCoords(numberParts) is GridCoordinate validCoords3)
                return validCoords3; // SUCCESS

            // Do the same for prefix stripped parts
            string[] prefixStrippedNumberParts = prefixStrippedParts.Where(p => double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out _)).ToArray();
            if (ParseValidCoords(prefixStrippedNumberParts) is GridCoordinate validCoords4)
                return validCoords4; // SUCCESS

            // As a last resort just use regex to look for any decimal numbers within the string. If there's two use them.
            Regex decimalRegex = DecimalNumber();
            MatchCollection matches = decimalRegex.Matches(infoString);
            string[] decimalParts = matches.Select(m => m.Value).ToArray();

            if (ParseValidCoords(decimalParts) is GridCoordinate validCoords5)
                return validCoords5; // SUCCESS

            // No success
            return null;
        }

        // Local function to parse 2 parts. Fails (returns null) if too many parts or the parts aren't just numbers
        static GridCoordinate? ParseValidCoords(string[] parts)
        {
            if (parts.Length != 2)
                return null;
            if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(parts[1], CultureInfo.InvariantCulture, out double y))
                return null;
            return new GridCoordinate(x, y);
        }

    } // End of ParseCoordinates

    private static bool CheckCoordinatesWithinBounds(GridCoordinate coordinates)
    {
        if (coordinates.X < 0 || coordinates.X > DefaultDivisions || coordinates.Y < 0 || coordinates.Y > DefaultDivisions)
            return false;
        else
            return true;
    }

    /// <summary>
    /// Calculates a contextual zoom region around the given coordinate and returns
    /// the zoomed <see cref="ViewRegion"/> for the next iteration.
    /// The zoom window extends ±1 grid cell on each axis from the coordinate.
    /// </summary>
    private static ViewRegion CalculateZoomRegion(
        ViewRegion currentView,
        GridCoordinate coordinate,
        int cols = DefaultDivisions,
        int rows = DefaultDivisions)
    {
        double spanX = 2.0;
        double spanY = 2.0;

        double xStart = coordinate.X - 1.0;
        if (xStart < 0)
            xStart = 0;
        else if (xStart + spanX > cols)
            xStart = cols - spanX;

        double yStart = coordinate.Y - 1.0;
        if (yStart < 0)
            yStart = 0;
        else if (yStart + spanY > rows)
            yStart = rows - spanY;

        return ZoomToRegion(currentView, xStart, yStart, spanX, spanY, cols, rows);
    }

    /// <summary>
    /// Converts grid coordinates on the current (possibly zoomed) view back to
    /// absolute screen pixel coordinates, assuming the original image matches screen resolution.
    /// </summary>
    private static (double ScreenX, double ScreenY) CalculateScreenCoordinates(
        ViewRegion currentView,
        GridCoordinate coordinate,
        int cols = DefaultDivisions,
        int rows = DefaultDivisions)
    {
        double screenX = currentView.X + (coordinate.X / cols) * currentView.Width;
        double screenY = currentView.Y + (coordinate.Y / rows) * currentView.Height;

        return (screenX, screenY);
    }

    /// <summary>
    /// Determines whether another zoom iteration is warranted based on the AI's 
    /// estimated precision in pixels compared to a confidence threshold.
    /// </summary>
    private static bool ShouldContinueZooming(
        ViewRegion currentView,
        int cols = DefaultDivisions,
        int rows = DefaultDivisions,
        double confidencePixels = DefaultConfidencePixels,
        double aiEstimatePrecision = DefaultAIEstimatePrecision)
    {
        double cellPixelW = currentView.Width / cols;
        double cellPixelH = currentView.Height / rows;

        // Calculate the actual pixel margin of error based on the AI's fractional accuracy
        double errorMarginX = cellPixelW * aiEstimatePrecision;
        double errorMarginY = cellPixelH * aiEstimatePrecision;

        // Continue zooming only if the margin of error is still larger than our confidence threshold
        return errorMarginX > confidencePixels || errorMarginY > confidencePixels;
    }

    /// <summary>Zooms the current view to a sub-region defined by grid-cell start and span.</summary>
    private static ViewRegion ZoomToRegion(
        ViewRegion currentView,
        double startX,
        double startY,
        double spanX,
        double spanY,
        int cols,
        int rows)
    {
        double cellPixelW = currentView.Width / cols;
        double cellPixelH = currentView.Height / rows;

        return new ViewRegion(
            currentView.X + startX * cellPixelW,
            currentView.Y + startY * cellPixelH,
            spanX * cellPixelW,
            spanY * cellPixelH);
    }


    [GeneratedRegex(@"\b[A-Z_]{2,}\b")]
    private static partial System.Text.RegularExpressions.Regex AllCapsCommandRegex();

    [GeneratedRegex(@"^[xy]\s*[=:]\s*", RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CoordPrefixRegex();

    [GeneratedRegex(@"\b\d+(\.\d+)\b")]
    private static partial System.Text.RegularExpressions.Regex DecimalNumber();
}
