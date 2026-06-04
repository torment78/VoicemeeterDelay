using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;

namespace VoicemeeterDelay;

public partial class RoundTripCalibrationWindow : Window
{
    private static readonly TimeSpan CalibrationPause = TimeSpan.FromMilliseconds(250);
    private const int FullCalibrationNoReturnSkipCount = 2;

    private readonly RoundTripCalibrationSettings _settings;
    private IReadOnlyList<AudioEndpointChoice> _pingInputs = [];
    private CancellationTokenSource? _measurementCancellation;
    private bool _updatingSelections;

    public RoundTripCalibrationWindow()
    {
        _settings = RoundTripCalibrationSettingsStore.Load();
        InitializeComponent();
        WaitSecondsTextBox.Text = _settings.WaitSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        CalibrationPingCountTextBox.Text = _settings.CalibrationPingCount.ToString(CultureInfo.InvariantCulture);
        RefreshEndpointChoices();
    }

    private void RefreshEndpointsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshEndpointChoices();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _measurementCancellation?.Cancel();
    }

    private void CopyResultsButton_Click(object sender, RoutedEventArgs e)
    {
        var text = GetResultText();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "There is no result text to copy.", "Round Trip Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PrintResultsButton_Click(object sender, RoutedEventArgs e)
    {
        var text = GetResultText();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "There is no result text to print.", "Round Trip Calibration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var savedAt = DateTime.Now;
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktopPath))
            {
                desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            var fileName = $"VoicemeeterDelay_RoundTrip_{savedAt:yyyy-MM-dd_HH-mm-ss}.txt";
            var filePath = Path.Combine(desktopPath, fileName);
            var fileText = "Voicemeeter Delay Round Trip Result" + Environment.NewLine
                + $"Saved: {savedAt:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine
                + Environment.NewLine
                + text
                + Environment.NewLine;

            File.WriteAllText(filePath, fileText, Encoding.UTF8);
            CalibrationStatusTextBlock.Text = text + Environment.NewLine + Environment.NewLine + $"Saved to: {filePath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Print failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateMeasurementRequest(out var request))
        {
            return;
        }

        SaveSelections();
        var cancellationSource = StartMeasurement();
        CalibrationStatusTextBlock.Text = BuildPingingStatus(request);

        try
        {
            var result = await MeasureAsync(request, cancellationSource.Token);
            CalibrationStatusTextBlock.Text = FormatSinglePingResult(result, request);
        }
        catch (OperationCanceledException)
        {
            CalibrationStatusTextBlock.Text = "Ping canceled.";
        }
        catch (Exception ex)
        {
            CalibrationStatusTextBlock.Text =
                "Ping failed." + Environment.NewLine
                + ex.Message;
        }
        finally
        {
            FinishMeasurement(cancellationSource);
        }
    }

    private async void CalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateMeasurementRequest(out var request)
            || !TryGetCalibrationPingCount(out var pingCount))
        {
            return;
        }

        SaveSelections();
        var cancellationSource = StartMeasurement();
        var results = new List<RoundTripMeasurementResult>(pingCount);

        try
        {
            for (var index = 0; index < pingCount; index++)
            {
                CalibrationStatusTextBlock.Text = BuildCalibrationProgressStatus(request, results, index + 1, pingCount);
                results.Add(await MeasureAsync(request, cancellationSource.Token));

                if (index + 1 < pingCount)
                {
                    await Task.Delay(CalibrationPause, cancellationSource.Token);
                }
            }

            CalibrationStatusTextBlock.Text = FormatCalibrationResult(results, request);
        }
        catch (OperationCanceledException)
        {
            CalibrationStatusTextBlock.Text = "MS test canceled.";
        }
        catch (Exception ex)
        {
            CalibrationStatusTextBlock.Text =
                "Calibration failed." + Environment.NewLine
                + ex.Message;
        }
        finally
        {
            FinishMeasurement(cancellationSource);
        }
    }

    private async void FullCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCreateFullCalibrationRequest(out var baseRequest)
            || !TryGetCalibrationPingCount(out var pingCount)
            || !TryGetFullCalibrationInputs(out var fullInputs))
        {
            return;
        }

        SaveSelections();
        var cancellationSource = StartMeasurement();
        var summaries = new List<FullCalibrationInputSummary>(fullInputs.Count);

        try
        {
            for (var inputIndex = 0; inputIndex < fullInputs.Count; inputIndex++)
            {
                var input = fullInputs[inputIndex];
                if (!TryResolvePingChannel(input, baseRequest.PingChannel, showError: false, out var resolvedInput, out var resolvedPingChannel))
                {
                    var unsupportedResults = new List<RoundTripMeasurementResult>
                    {
                        new(
                            input.Name,
                            baseRequest.ReturnOutput.Name,
                            Detected: false,
                            RoundTripMilliseconds: 0,
                            DetectionLevel: 0,
                            Message: BuildUnsupportedPingChannelMessage(input, baseRequest.PingChannel))
                    };
                    summaries.Add(new FullCalibrationInputSummary(input.Name, baseRequest.PingChannel.Name, BuildStats(unsupportedResults)));
                    continue;
                }

                var request = baseRequest with
                {
                    PingInput = resolvedInput,
                    PingChannel = resolvedPingChannel
                };
                var results = new List<RoundTripMeasurementResult>(pingCount);

                for (var pingIndex = 0; pingIndex < pingCount; pingIndex++)
                {
                    CalibrationStatusTextBlock.Text = BuildFullCalibrationProgressStatus(
                        baseRequest,
                        summaries,
                        input,
                        inputIndex + 1,
                        fullInputs.Count,
                        pingIndex + 1,
                        pingCount,
                        results);

                    results.Add(await MeasureAsync(request, cancellationSource.Token));

                    if (ShouldSkipFullCalibrationInput(results))
                    {
                        break;
                    }

                    if (pingIndex + 1 < pingCount)
                    {
                        await Task.Delay(CalibrationPause, cancellationSource.Token);
                    }
                }

                var stats = BuildStats(results);
                summaries.Add(new FullCalibrationInputSummary(input.Name, baseRequest.PingChannel.Name, stats));

                if (inputIndex + 1 < fullInputs.Count)
                {
                    await Task.Delay(CalibrationPause, cancellationSource.Token);
                }
            }

            CalibrationStatusTextBlock.Text = FormatFullCalibrationResult(summaries, baseRequest, pingCount);
        }
        catch (OperationCanceledException)
        {
            CalibrationStatusTextBlock.Text = FormatFullCalibrationCanceledResult(summaries);
        }
        catch (Exception ex)
        {
            CalibrationStatusTextBlock.Text =
                "Full calibration failed." + Environment.NewLine
                + ex.Message;
        }
        finally
        {
            FinishMeasurement(cancellationSource);
        }
    }

    private static Task<RoundTripMeasurementResult> MeasureAsync(
        MeasurementRequest request,
        CancellationToken cancellationToken)
    {
        return RoundTripMeasurementEngine.MeasureAsync(
            request.PingInput,
            request.ReturnOutput,
            request.PingChannel.ChannelIndex,
            request.ReturnChannel.ChannelIndex,
            request.WaitSeconds,
            cancellationToken);
    }

    private string GetResultText()
    {
        return CalibrationStatusTextBlock.Text?.Trim() ?? string.Empty;
    }

    private CancellationTokenSource StartMeasurement()
    {
        _measurementCancellation?.Cancel();
        _measurementCancellation?.Dispose();
        _measurementCancellation = new CancellationTokenSource();
        SetBusy(true);
        return _measurementCancellation;
    }

    private void FinishMeasurement(CancellationTokenSource cancellationSource)
    {
        if (ReferenceEquals(_measurementCancellation, cancellationSource))
        {
            _measurementCancellation = null;
        }

        cancellationSource.Dispose();
        SetBusy(false);
    }

    private static bool ShouldSkipFullCalibrationInput(IReadOnlyList<RoundTripMeasurementResult> results)
    {
        return results.Count >= FullCalibrationNoReturnSkipCount
            && !results.Any(static result => result.Detected);
    }

    private bool TryCreateMeasurementRequest(out MeasurementRequest request)
    {
        request = default!;
        if (PingInputComboBox.SelectedItem is not AudioEndpointChoice pingInput
            || ReturnOutputComboBox.SelectedItem is not AudioEndpointChoice returnOutput
            || PingChannelComboBox.SelectedItem is not AudioChannelChoice pingChannel
            || ReturnChannelComboBox.SelectedItem is not AudioChannelChoice returnChannel)
        {
            CalibrationStatusTextBlock.Text = "Pick a Voicemeeter input, output, and channel.";
            return false;
        }

        if (!TryGetWaitSeconds(out var waitSeconds))
        {
            return false;
        }

        if (!TryResolvePingChannel(pingInput, pingChannel, showError: true, out var resolvedPingInput, out var resolvedPingChannel))
        {
            return false;
        }

        request = new MeasurementRequest(resolvedPingInput, returnOutput, resolvedPingChannel, returnChannel, waitSeconds);
        return true;
    }

    private bool TryCreateFullCalibrationRequest(out MeasurementRequest request)
    {
        request = default!;
        if (PingInputComboBox.SelectedItem is not AudioEndpointChoice pingInput
            || FullReturnOutputComboBox.SelectedItem is not AudioEndpointChoice returnOutput
            || PingChannelComboBox.SelectedItem is not AudioChannelChoice pingChannel
            || FullReturnChannelComboBox.SelectedItem is not AudioChannelChoice returnChannel)
        {
            CalibrationStatusTextBlock.Text = "Pick a Voicemeeter input, full output, ping channel, and full return channel.";
            return false;
        }

        if (!TryGetWaitSeconds(out var waitSeconds))
        {
            return false;
        }

        request = new MeasurementRequest(pingInput, returnOutput, pingChannel, returnChannel, waitSeconds);
        return true;
    }

    private bool TryResolvePingChannel(
        AudioEndpointChoice pingInput,
        AudioChannelChoice pingChannel,
        bool showError,
        out AudioEndpointChoice resolvedInput,
        out AudioChannelChoice resolvedChannel)
    {
        resolvedInput = pingInput;
        resolvedChannel = pingChannel;
        if (pingChannel.ChannelIndex < 2)
        {
            return true;
        }

        if (TryMapLogicalStereoPair(pingInput, pingChannel.ChannelIndex, out var mappedInput, out var mappedChannelIndex))
        {
            resolvedInput = mappedInput;
            resolvedChannel = new AudioChannelChoice(
                mappedChannelIndex,
                $"{pingChannel.Name} via {mappedInput.Name} {GetChannelName(mappedChannelIndex)}");
            return true;
        }

        if (showError)
        {
            CalibrationStatusTextBlock.Text = BuildUnsupportedPingChannelMessage(pingInput, pingChannel);
        }

        return false;
    }

    private bool TryMapLogicalStereoPair(
        AudioEndpointChoice pingInput,
        int logicalChannelIndex,
        out AudioEndpointChoice mappedInput,
        out int mappedChannelIndex)
    {
        mappedInput = pingInput;
        mappedChannelIndex = logicalChannelIndex;
        if (!TryReadStereoPairEndpoint(pingInput.Name, out var familyName, out _))
        {
            return false;
        }

        var targetPairNumber = (logicalChannelIndex / 2) + 1;
        mappedChannelIndex = logicalChannelIndex % 2;
        foreach (var candidate in _pingInputs)
        {
            if (TryReadStereoPairEndpoint(candidate.Name, out var candidateFamilyName, out var candidatePairNumber)
                && candidatePairNumber == targetPairNumber
                && string.Equals(candidateFamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            {
                mappedInput = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadStereoPairEndpoint(string endpointName, out string familyName, out int pairNumber)
    {
        familyName = string.Empty;
        pairNumber = 0;
        var index = endpointName.Length - 1;
        while (index >= 0 && char.IsDigit(endpointName[index]))
        {
            index--;
        }

        if (index == endpointName.Length - 1
            || !int.TryParse(endpointName[(index + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out pairNumber))
        {
            return false;
        }

        var prefix = endpointName[..(index + 1)].TrimEnd();
        var normalizedPrefix = NormalizeEndpointName(prefix);
        if (normalizedPrefix is not ("voicemeeterin" or "vbmatrixin"))
        {
            return false;
        }

        familyName = normalizedPrefix;
        return true;
    }

    private static string BuildUnsupportedPingChannelMessage(AudioEndpointChoice pingInput, AudioChannelChoice pingChannel)
    {
        return $"{pingInput.Name} is exposed to Windows as a stereo endpoint, so {pingChannel.Name} cannot be played directly from that device."
            + Environment.NewLine
            + "For numbered extension pairs, choose the In 1 family and logical channels 3-8 will map to In 2/In 3/In 4 pairs.";
    }

    private static string BuildPingingStatus(MeasurementRequest request)
    {
        return "Pinging..." + Environment.NewLine
            + FormatRoute(request) + Environment.NewLine
            + $"Waiting up to {request.WaitSeconds:0.#} seconds.";
    }

    private static string BuildCalibrationProgressStatus(
        MeasurementRequest request,
        IReadOnlyList<RoundTripMeasurementResult> completedResults,
        int currentPing,
        int pingCount)
    {
        var builder = new StringBuilder()
            .AppendLine($"Calibration ping {currentPing} of {pingCount}...")
            .AppendLine(FormatRoute(request))
            .AppendLine($"Waiting up to {request.WaitSeconds:0.#} seconds per ping.");

        if (completedResults.Count > 0)
        {
            builder.AppendLine()
                .Append("Completed: ")
                .Append(FormatCompactResults(completedResults));
        }

        return builder.ToString();
    }

    private static string FormatSinglePingResult(
        RoundTripMeasurementResult result,
        MeasurementRequest request)
    {
        return result.Detected
            ? $"Round trip: {result.RoundTripMilliseconds:0.0} ms" + Environment.NewLine
                + FormatRoute(request) + Environment.NewLine
                + $"Detection level: {result.DetectionLevel:0.000}"
            : result.Message + Environment.NewLine
                + FormatRoute(request) + Environment.NewLine
                + $"Wait: {request.WaitSeconds:0.#} seconds" + Environment.NewLine
                + "Check that the selected Voicemeeter input is routed to the selected return bus/output.";
    }

    private static string FormatCalibrationResult(
        IReadOnlyList<RoundTripMeasurementResult> results,
        MeasurementRequest request)
    {
        var stats = BuildStats(results);

        if (!stats.HasDetected)
        {
            return "MS test failed: no ping returns detected." + Environment.NewLine
                + FormatRoute(request) + Environment.NewLine
                + $"Missed: {stats.MissedCount} of {stats.TotalCount}" + Environment.NewLine
                + FormatCaptureDiagnostics(stats);
        }

        return "MS test complete." + Environment.NewLine
            + FormatRoute(request) + Environment.NewLine
            + $"Detected: {stats.DetectedCount} of {stats.TotalCount}" + Environment.NewLine
            + $"Average: {stats.Average:0.0} ms" + Environment.NewLine
            + $"Median: {stats.Median:0.0} ms" + Environment.NewLine
            + $"Min / Max: {stats.Min:0.0} ms / {stats.Max:0.0} ms" + Environment.NewLine
            + $"Spread: {stats.Spread:0.0} ms" + Environment.NewLine
            + $"Missed: {stats.MissedCount}" + Environment.NewLine
            + $"Values: {stats.Values} ms";
    }

    private static string BuildFullCalibrationProgressStatus(
        MeasurementRequest baseRequest,
        IReadOnlyList<FullCalibrationInputSummary> completedInputs,
        AudioEndpointChoice currentInput,
        int currentInputNumber,
        int inputCount,
        int currentPing,
        int pingCount,
        IReadOnlyList<RoundTripMeasurementResult> currentResults)
    {
        var builder = new StringBuilder()
            .AppendLine($"Full calibration input {currentInputNumber} of {inputCount}, ping {currentPing} of {pingCount}...")
            .AppendLine($"Input: {currentInput.Name}")
            .AppendLine($"Ping channel: {baseRequest.PingChannel.Name}")
            .AppendLine($"Output: {baseRequest.ReturnOutput.Name}")
            .AppendLine($"Return channel: {baseRequest.ReturnChannel.Name}");

        if (currentResults.Count > 0)
        {
            builder.AppendLine()
                .Append("Current input: ")
                .AppendLine(FormatCompactResults(currentResults));
        }

        if (completedInputs.Count > 0)
        {
            builder.AppendLine()
                .AppendLine("Completed inputs:");
            foreach (var completedInput in completedInputs)
            {
                builder.AppendLine(FormatFullCalibrationSummaryLine(completedInput));
            }
        }

        return builder.ToString();
    }

    private static string FormatFullCalibrationResult(
        IReadOnlyList<FullCalibrationInputSummary> summaries,
        MeasurementRequest baseRequest,
        int pingCount)
    {
        if (summaries.Count == 0)
        {
            return "Full calibration did not run any inputs.";
        }

        var builder = new StringBuilder()
            .AppendLine("Full calibration complete.")
            .AppendLine($"Ping channel: {baseRequest.PingChannel.Name}")
            .AppendLine($"Output: {baseRequest.ReturnOutput.Name}")
            .AppendLine($"Return channel: {baseRequest.ReturnChannel.Name}")
            .AppendLine($"Pings per input: up to {pingCount}")
            .AppendLine($"No-return skip: after {FullCalibrationNoReturnSkipCount} misses")
            .AppendLine($"Inputs: {summaries.Count}")
            .AppendLine()
            .AppendLine("Summary:");

        foreach (var summary in summaries)
        {
            builder.AppendLine(FormatFullCalibrationSummaryLine(summary));
            if (!string.IsNullOrWhiteSpace(summary.Stats.Values))
            {
                builder.AppendLine($"  Values: {summary.Stats.Values} ms");
            }
        }

        return builder.ToString();
    }

    private static string FormatFullCalibrationCanceledResult(IReadOnlyList<FullCalibrationInputSummary> summaries)
    {
        if (summaries.Count == 0)
        {
            return "Full calibration canceled.";
        }

        var builder = new StringBuilder()
            .AppendLine("Full calibration canceled.")
            .AppendLine("Completed inputs:");

        foreach (var summary in summaries)
        {
            builder.AppendLine(FormatFullCalibrationSummaryLine(summary));
        }

        return builder.ToString();
    }

    private static string FormatFullCalibrationSummaryLine(FullCalibrationInputSummary summary)
    {
        var stats = summary.Stats;
        if (!stats.HasDetected)
        {
            return $"{summary.InputName} {summary.PingChannelName}: no return, missed {stats.MissedCount} of {stats.TotalCount}, {FormatCaptureDiagnostics(stats)}";
        }

        return $"{summary.InputName} {summary.PingChannelName}: avg {stats.Average:0.0} ms, median {stats.Median:0.0} ms, "
            + $"min/max {stats.Min:0.0}/{stats.Max:0.0}, spread {stats.Spread:0.0}, "
            + $"detected {stats.DetectedCount} of {stats.TotalCount}";
    }

    private static CalibrationStats BuildStats(IReadOnlyList<RoundTripMeasurementResult> results)
    {
        var detectedValues = results
            .Where(static result => result.Detected)
            .Select(static result => result.RoundTripMilliseconds)
            .Order()
            .ToArray();
        var highestLevel = results.Count == 0
            ? 0.0
            : results.Max(static result => result.DetectionLevel);
        var bestCaptureResult = results
            .OrderByDescending(static result => result.BestCaptureChannelLevel)
            .FirstOrDefault();
        var bestCaptureChannelIndex = bestCaptureResult?.BestCaptureChannelIndex ?? -1;
        var bestCaptureChannelLevel = bestCaptureResult?.BestCaptureChannelLevel ?? 0.0;
        if (detectedValues.Length == 0)
        {
            return new CalibrationStats(
                results.Count,
                DetectedCount: 0,
                MissedCount: results.Count,
                Average: 0,
                Median: 0,
                Min: 0,
                Max: 0,
                Spread: 0,
                highestLevel,
                Values: string.Empty,
                bestCaptureChannelIndex,
                bestCaptureChannelLevel);
        }

        var min = detectedValues.First();
        var max = detectedValues.Last();
        return new CalibrationStats(
            results.Count,
            detectedValues.Length,
            results.Count - detectedValues.Length,
            detectedValues.Average(),
            GetMedian(detectedValues),
            min,
            max,
            max - min,
            highestLevel,
            string.Join(", ", detectedValues.Select(static value => value.ToString("0.0", CultureInfo.InvariantCulture))),
            bestCaptureChannelIndex,
            bestCaptureChannelLevel);
    }

    private static string FormatCaptureDiagnostics(CalibrationStats stats)
    {
        var message = $"selected channel highest {stats.HighestLevel:0.000}";
        if (stats.BestCaptureChannelIndex < 0)
        {
            return message + ", no signal captured on selected return device";
        }

        return message
            + $", strongest captured Ch {stats.BestCaptureChannelIndex + 1} at {stats.BestCaptureChannelLevel:0.000}";
    }

    private static string FormatCompactResults(IReadOnlyList<RoundTripMeasurementResult> results)
    {
        return string.Join(
            ", ",
            results.Select(static result => result.Detected
                ? result.RoundTripMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms"
                : "miss"));
    }

    private static string FormatRoute(MeasurementRequest request)
    {
        return $"Input: {request.PingInput.Name}" + Environment.NewLine
            + $"Ping channel: {request.PingChannel.Name}" + Environment.NewLine
            + $"Output: {request.ReturnOutput.Name}" + Environment.NewLine
            + $"Return channel: {request.ReturnChannel.Name}";
    }

    private static double GetMedian(IReadOnlyList<double> sortedValues)
    {
        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2.0
            : sortedValues[middle];
    }

    private void RefreshEndpointChoices()
    {
        try
        {
            var preferredPingInputId = (PingInputComboBox.SelectedItem as AudioEndpointChoice)?.Id ?? _settings.PingInputId;
            var preferredReturnOutputId = (ReturnOutputComboBox.SelectedItem as AudioEndpointChoice)?.Id ?? _settings.ReturnOutputId;
            var preferredFullStartInputId =
                (FullStartInputComboBox.SelectedItem as AudioEndpointChoice)?.Id
                ?? _settings.FullCalibrationStartInputId
                ?? preferredPingInputId;
            var preferredFullEndInputId =
                (FullEndInputComboBox.SelectedItem as AudioEndpointChoice)?.Id
                ?? _settings.FullCalibrationEndInputId;
            var preferredFullReturnOutputId =
                (FullReturnOutputComboBox.SelectedItem as AudioEndpointChoice)?.Id
                ?? _settings.FullCalibrationReturnOutputId
                ?? preferredReturnOutputId;
            var pingInputs = AudioSessionDiscovery.GetVoicemeeterEndpointChoices(AudioEndpointFlow.Playback);
            var returnOutputs = AudioSessionDiscovery.GetVoicemeeterEndpointChoices(AudioEndpointFlow.Capture);
            _pingInputs = pingInputs;

            _updatingSelections = true;
            try
            {
                PingInputComboBox.ItemsSource = pingInputs;
                ReturnOutputComboBox.ItemsSource = returnOutputs;
                FullStartInputComboBox.ItemsSource = pingInputs;
                FullEndInputComboBox.ItemsSource = pingInputs;
                FullReturnOutputComboBox.ItemsSource = returnOutputs;

                SelectEndpoint(PingInputComboBox, pingInputs, preferredPingInputId);
                SelectEndpoint(ReturnOutputComboBox, returnOutputs, preferredReturnOutputId);
                SelectEndpoint(FullStartInputComboBox, pingInputs, preferredFullStartInputId);
                SelectEndpoint(FullEndInputComboBox, pingInputs, preferredFullEndInputId, Math.Max(0, pingInputs.Count - 1));
                SelectEndpoint(FullReturnOutputComboBox, returnOutputs, preferredFullReturnOutputId);

                UpdateChannelChoices(PingChannelComboBox, PingInputComboBox.SelectedItem as AudioEndpointChoice, _settings.PingChannelIndex);
                UpdateChannelChoices(ReturnChannelComboBox, ReturnOutputComboBox.SelectedItem as AudioEndpointChoice, _settings.ReturnChannelIndex);
                UpdateChannelChoices(FullReturnChannelComboBox, FullReturnOutputComboBox.SelectedItem as AudioEndpointChoice, _settings.FullCalibrationReturnChannelIndex);
            }
            finally
            {
                _updatingSelections = false;
            }

            UpdateActionButtonsAvailability();
            CalibrationStatusTextBlock.Text =
                $"Found {pingInputs.Count} Voicemeeter input device(s) and {returnOutputs.Count} Voicemeeter output device(s).";
        }
        catch (AccessViolationException ex)
        {
            ClearEndpointChoices(
                "Audio endpoint refresh failed because Windows audio returned invalid native memory." + Environment.NewLine
                + ex.Message);
        }
        catch (Exception ex)
        {
            PingButton.IsEnabled = false;
            CalibrationButton.IsEnabled = false;
            FullCalibrationButton.IsEnabled = false;
            CalibrationStatusTextBlock.Text = ex.Message;
        }
    }

    private void ClearEndpointChoices(string statusText)
    {
        _pingInputs = [];
        _updatingSelections = true;
        try
        {
            PingInputComboBox.ItemsSource = Array.Empty<AudioEndpointChoice>();
            ReturnOutputComboBox.ItemsSource = Array.Empty<AudioEndpointChoice>();
            FullStartInputComboBox.ItemsSource = Array.Empty<AudioEndpointChoice>();
            FullEndInputComboBox.ItemsSource = Array.Empty<AudioEndpointChoice>();
            FullReturnOutputComboBox.ItemsSource = Array.Empty<AudioEndpointChoice>();
            PingChannelComboBox.ItemsSource = Array.Empty<AudioChannelChoice>();
            ReturnChannelComboBox.ItemsSource = Array.Empty<AudioChannelChoice>();
            FullReturnChannelComboBox.ItemsSource = Array.Empty<AudioChannelChoice>();
        }
        finally
        {
            _updatingSelections = false;
        }

        UpdateActionButtonsAvailability();
        CalibrationStatusTextBlock.Text = statusText;
    }

    private void SetBusy(bool busy)
    {
        if (busy)
        {
            PingButton.IsEnabled = false;
            CalibrationButton.IsEnabled = false;
            FullCalibrationButton.IsEnabled = false;
        }
        else
        {
            UpdateActionButtonsAvailability();
        }

        RefreshEndpointsButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PingInputComboBox.IsEnabled = !busy;
        ReturnOutputComboBox.IsEnabled = !busy;
        PingChannelComboBox.IsEnabled = !busy;
        ReturnChannelComboBox.IsEnabled = !busy;
        FullReturnOutputComboBox.IsEnabled = !busy;
        FullReturnChannelComboBox.IsEnabled = !busy;
        WaitSecondsTextBox.IsEnabled = !busy;
        CalibrationPingCountTextBox.IsEnabled = !busy;
        FullStartInputComboBox.IsEnabled = !busy;
        FullEndInputComboBox.IsEnabled = !busy;
    }

    private void UpdateActionButtonsAvailability()
    {
        var canMeasure = PingInputComboBox.SelectedItem is not null
            && ReturnOutputComboBox.SelectedItem is not null
            && PingChannelComboBox.SelectedItem is not null
            && ReturnChannelComboBox.SelectedItem is not null;
        var canFullCalibrate = canMeasure
            && FullStartInputComboBox.SelectedItem is not null
            && FullEndInputComboBox.SelectedItem is not null
            && FullReturnOutputComboBox.SelectedItem is not null
            && FullReturnChannelComboBox.SelectedItem is not null;
        PingButton.IsEnabled = canMeasure;
        CalibrationButton.IsEnabled = canMeasure;
        FullCalibrationButton.IsEnabled = canFullCalibrate;
    }

    private void EndpointComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections)
        {
            return;
        }

        if (sender == PingInputComboBox)
        {
            var preferredChannel = GetPreferredChannelIndex(PingChannelComboBox, _settings.PingChannelIndex);
            UpdateChannelChoices(PingChannelComboBox, PingInputComboBox.SelectedItem as AudioEndpointChoice, preferredChannel);
        }

        if (sender == ReturnOutputComboBox)
        {
            var preferredChannel = GetPreferredChannelIndex(ReturnChannelComboBox, _settings.ReturnChannelIndex);
            UpdateChannelChoices(ReturnChannelComboBox, ReturnOutputComboBox.SelectedItem as AudioEndpointChoice, preferredChannel);
        }

        if (sender == FullReturnOutputComboBox)
        {
            var preferredFullReturnChannel = GetPreferredChannelIndex(FullReturnChannelComboBox, _settings.FullCalibrationReturnChannelIndex);
            UpdateChannelChoices(FullReturnChannelComboBox, FullReturnOutputComboBox.SelectedItem as AudioEndpointChoice, preferredFullReturnChannel);
        }

        SaveSelections();
        UpdateActionButtonsAvailability();
    }

    private void ChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveSelections();
    }

    private void WaitSecondsTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryGetWaitSeconds(out _))
        {
            SaveSelections();
        }
    }

    private void CalibrationPingCountTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryGetCalibrationPingCount(out _))
        {
            SaveSelections();
        }
    }

    private bool TryGetFullCalibrationInputs(out IReadOnlyList<AudioEndpointChoice> inputs)
    {
        inputs = [];
        if (FullStartInputComboBox.SelectedItem is not AudioEndpointChoice startInput
            || FullEndInputComboBox.SelectedItem is not AudioEndpointChoice endInput)
        {
            CalibrationStatusTextBlock.Text = "Pick full calibration start and finish inputs.";
            return false;
        }

        var startIndex = FindEndpointIndex(_pingInputs, startInput.Id);
        var endIndex = FindEndpointIndex(_pingInputs, endInput.Id);
        if (startIndex < 0 || endIndex < 0)
        {
            CalibrationStatusTextBlock.Text = "Refresh endpoints and pick the full calibration input range again.";
            return false;
        }

        var firstIndex = Math.Min(startIndex, endIndex);
        var lastIndex = Math.Max(startIndex, endIndex);
        inputs = _pingInputs
            .Skip(firstIndex)
            .Take(lastIndex - firstIndex + 1)
            .ToArray();
        return inputs.Count > 0;
    }

    private static int FindEndpointIndex(IReadOnlyList<AudioEndpointChoice> endpoints, string endpointId)
    {
        for (var index = 0; index < endpoints.Count; index++)
        {
            if (string.Equals(endpoints[index].Id, endpointId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void SelectEndpoint(
        ComboBox comboBox,
        IReadOnlyList<AudioEndpointChoice> endpoints,
        string? preferredEndpointId,
        int fallbackIndex = 0)
    {
        var fallbackEndpoint = endpoints.Count == 0
            ? null
            : endpoints[Math.Clamp(fallbackIndex, 0, endpoints.Count - 1)];
        comboBox.SelectedItem = !string.IsNullOrWhiteSpace(preferredEndpointId)
            ? endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Id, preferredEndpointId, StringComparison.Ordinal))
                ?? fallbackEndpoint
            : fallbackEndpoint;
    }

    private static int GetPreferredChannelIndex(ComboBox comboBox, int savedChannelIndex)
    {
        return comboBox.SelectedItem is AudioChannelChoice channel
            ? channel.ChannelIndex
            : savedChannelIndex;
    }

    private void UpdateChannelChoices(ComboBox comboBox, AudioEndpointChoice? endpoint, int preferredChannelIndex)
    {
        var channelCount = GetEndpointChannelCount(endpoint);
        var choices = CreateChannelChoices(channelCount);
        comboBox.ItemsSource = choices;
        comboBox.SelectedItem = choices.FirstOrDefault(choice => choice.ChannelIndex == preferredChannelIndex)
            ?? choices.FirstOrDefault();
    }

    private static int GetEndpointChannelCount(AudioEndpointChoice? endpoint)
    {
        if (endpoint is null)
        {
            return 0;
        }

        // Voicemeeter round-trip devices are exposed to Windows as stereo endpoints.
        return 2;
    }

    private static IReadOnlyList<AudioChannelChoice> CreateChannelChoices(int channelCount)
    {
        if (channelCount <= 0)
        {
            return [];
        }

        return Enumerable
            .Range(0, Math.Min(channelCount, 8))
            .Select(static channelIndex => new AudioChannelChoice(channelIndex, GetChannelName(channelIndex)))
            .ToArray();
    }

    private static string GetChannelName(int channelIndex)
    {
        return channelIndex switch
        {
            0 => "Ch 1 Left",
            1 => "Ch 2 Right",
            _ => $"Ch {channelIndex + 1}"
        };
    }

    private static string NormalizeEndpointName(string endpointName)
    {
        return new string(endpointName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private void SaveSelections()
    {
        if (_updatingSelections)
        {
            return;
        }

        _settings.PingInputId = (PingInputComboBox.SelectedItem as AudioEndpointChoice)?.Id;
        _settings.ReturnOutputId = (ReturnOutputComboBox.SelectedItem as AudioEndpointChoice)?.Id;
        _settings.FullCalibrationStartInputId = (FullStartInputComboBox.SelectedItem as AudioEndpointChoice)?.Id;
        _settings.FullCalibrationEndInputId = (FullEndInputComboBox.SelectedItem as AudioEndpointChoice)?.Id;
        _settings.FullCalibrationReturnOutputId = (FullReturnOutputComboBox.SelectedItem as AudioEndpointChoice)?.Id;
        _settings.PingChannelIndex = GetPreferredChannelIndex(PingChannelComboBox, _settings.PingChannelIndex);
        _settings.ReturnChannelIndex = GetPreferredChannelIndex(ReturnChannelComboBox, _settings.ReturnChannelIndex);
        _settings.FullCalibrationReturnChannelIndex = GetPreferredChannelIndex(FullReturnChannelComboBox, _settings.FullCalibrationReturnChannelIndex);
        if (TryReadWaitSeconds(showError: false, out var waitSeconds))
        {
            _settings.WaitSeconds = waitSeconds;
        }

        if (TryReadCalibrationPingCount(showError: false, out var calibrationPingCount))
        {
            _settings.CalibrationPingCount = calibrationPingCount;
        }

        RoundTripCalibrationSettingsStore.Save(_settings);
    }

    private bool TryGetWaitSeconds(out double waitSeconds)
    {
        var isValid = TryReadWaitSeconds(showError: true, out waitSeconds);
        if (isValid)
        {
            _settings.WaitSeconds = waitSeconds;
        }

        return isValid;
    }

    private bool TryReadWaitSeconds(bool showError, out double waitSeconds)
    {
        if (!double.TryParse(WaitSecondsTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out waitSeconds)
            && !double.TryParse(WaitSecondsTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out waitSeconds))
        {
            if (showError)
            {
                CalibrationStatusTextBlock.Text = "Wait must be a number of seconds.";
            }

            waitSeconds = _settings.WaitSeconds;
            return false;
        }

        if (waitSeconds < 1 || waitSeconds > 30)
        {
            if (showError)
            {
                CalibrationStatusTextBlock.Text = "Wait must be between 1 and 30 seconds.";
            }

            return false;
        }

        WaitSecondsTextBox.Text = waitSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        return true;
    }

    private bool TryGetCalibrationPingCount(out int pingCount)
    {
        var isValid = TryReadCalibrationPingCount(showError: true, out pingCount);
        if (isValid)
        {
            _settings.CalibrationPingCount = pingCount;
        }

        return isValid;
    }

    private bool TryReadCalibrationPingCount(bool showError, out int pingCount)
    {
        if (!int.TryParse(CalibrationPingCountTextBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out pingCount)
            && !int.TryParse(CalibrationPingCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out pingCount))
        {
            if (showError)
            {
                CalibrationStatusTextBlock.Text = "Pings must be a whole number.";
            }

            pingCount = _settings.CalibrationPingCount;
            return false;
        }

        if (pingCount < 2 || pingCount > 50)
        {
            if (showError)
            {
                CalibrationStatusTextBlock.Text = "Pings must be between 2 and 50.";
            }

            return false;
        }

        CalibrationPingCountTextBox.Text = pingCount.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}

internal sealed record AudioChannelChoice(int ChannelIndex, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}

internal sealed record MeasurementRequest(
    AudioEndpointChoice PingInput,
    AudioEndpointChoice ReturnOutput,
    AudioChannelChoice PingChannel,
    AudioChannelChoice ReturnChannel,
    double WaitSeconds);

internal sealed record FullCalibrationInputSummary(
    string InputName,
    string PingChannelName,
    CalibrationStats Stats);

internal sealed record CalibrationStats(
    int TotalCount,
    int DetectedCount,
    int MissedCount,
    double Average,
    double Median,
    double Min,
    double Max,
    double Spread,
    double HighestLevel,
    string Values,
    int BestCaptureChannelIndex = -1,
    double BestCaptureChannelLevel = 0)
{
    public bool HasDetected => DetectedCount > 0;
}
