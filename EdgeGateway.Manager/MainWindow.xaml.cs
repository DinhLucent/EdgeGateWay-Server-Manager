using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Health;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Probe;
using EdgeGateway.Core.Runtime;
using EdgeGateway.Core.Security;
using EdgeGateway.Manager.Api;
using EdgeGateway.Manager.Models;
using Microsoft.Win32;

namespace EdgeGateway.Manager;

public partial class MainWindow : Window
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(22, 163, 74));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));

    private readonly EdgeGatewayApiClient apiClient;
    private readonly ObservableCollection<DeviceViewModel> devices = [];
    private readonly ObservableCollection<DashboardDeviceRow> dashboardRows = [];
    private readonly ObservableCollection<LogFileViewModel> deviceLogFiles = [];
    private readonly ObservableCollection<LogFileViewModel> exceptionFiles = [];
    private readonly ObservableCollection<CapturedFileSummary> capturedFiles = [];
    private readonly ObservableCollection<PreparedOrderRow> preparedOrders = [];
    private readonly ObservableCollection<PacketLogRow> packetLogRows = [];
    private readonly ObservableCollection<OutputQueueRow> outputQueueRows = [];
    private readonly DispatcherTimer refreshTimer = new();
    private readonly bool ownsApiClient;
    private string? profileLibraryRoot;

    private DeviceConfiguration? configuration;
    private EdgeGatewayAccessContext? accessContext;
    private bool refreshInProgress;
    private bool editMode;

    public MainWindow()
        : this(CreateDefaultApiClient(), ownsApiClient: true)
    {
    }

    internal MainWindow(EdgeGatewayApiClient sharedApiClient, bool ownsApiClient = false)
    {
        InitializeComponent();
        apiClient = sharedApiClient;
        this.ownsApiClient = ownsApiClient;

        TransportComboBox.ItemsSource = Enum.GetValues<DeviceTransport>();
        ProtocolComboBox.ItemsSource = Enum.GetValues<DeviceProtocol>();
        LoadLocalProfilePresets();
        ParityComboBox.ItemsSource = Enum.GetValues<SerialPortParity>();
        StopBitsComboBox.ItemsSource = Enum.GetValues<SerialPortStopBits>();
        FlowControlComboBox.ItemsSource = Enum.GetValues<SerialPortFlowControl>();
        RawBoundaryModeComboBox.ItemsSource = Enum.GetValues<RawBoundaryMode>();
        RawDelimiterComboBox.ItemsSource = new[] { "CRLF", "CR", "LF", "custom" };
        AstmDirectionComboBox.ItemsSource = Enum.GetValues<AstmSessionDirection>();
        AstmChecksumModeComboBox.ItemsSource = Enum.GetValues<AstmChecksumMode>();
        AstmFrameNumberModeComboBox.ItemsSource = Enum.GetValues<AstmFrameNumberMode>();
        AstmDuplicateFramePolicyComboBox.ItemsSource = Enum.GetValues<AstmDuplicateFramePolicy>();
        AstmEnqCollisionPolicyComboBox.ItemsSource = Enum.GetValues<AstmEnqCollisionPolicy>();
        AstmUnknownQueryPolicyComboBox.ItemsSource = Enum.GetValues<AstmUnknownQueryPolicy>();
        OutputTypeComboBox.ItemsSource = Enum.GetValues<OutputTargetType>();
        DashboardDevicesGrid.ItemsSource = dashboardRows;
        DevicesGrid.ItemsSource = devices;
        DeviceLogFilesListBox.ItemsSource = deviceLogFiles;
        ExceptionFilesListBox.ItemsSource = exceptionFiles;
        CapturedFilesListBox.ItemsSource = capturedFiles;
        OrdersGrid.ItemsSource = preparedOrders;
        PacketLogGrid.ItemsSource = packetLogRows;
        OutputQueueGrid.ItemsSource = outputQueueRows;
        PacketDirectionComboBox.ItemsSource = new[] { "All", "RX", "TX", "SYS", "ERR" };
        PacketDirectionComboBox.SelectedIndex = 0;
        OutputStatusFilterComboBox.ItemsSource = new[]
        {
            "All",
            "Pending",
            "Retrying",
            "Dead Letter",
            "Failed",
            "Sent",
        };
        OutputStatusFilterComboBox.SelectedIndex = 0;
        LogCategoryComboBox.ItemsSource = new[] { "System", "Device", "Data" };
        LogCategoryComboBox.SelectedIndex = 1;
        LogLevelComboBox.ItemsSource = new[] { "Info", "Debug", "Trace", "Warning", "Error" };
        LogLevelComboBox.SelectedIndex = 0;

        refreshTimer.Tick += async (_, _) => await RefreshAllAsync();
        Loaded += async (_, _) => await LoadInitialStateAsync();
        Closed += (_, _) =>
        {
            if (this.ownsApiClient)
            {
                apiClient.Dispose();
            }
        };
    }

    private static EdgeGatewayApiClient CreateDefaultApiClient()
    {
        string apiUrl = Environment.GetEnvironmentVariable("EDGE_GATEWAY_MANAGER_API_URL")
            ?? "http://127.0.0.1:18082";
        return new EdgeGatewayApiClient(
            new Uri(apiUrl),
            Environment.GetEnvironmentVariable("EDGE_GATEWAY_MANAGER_ROLE")
        );
    }

    private async Task LoadInitialStateAsync()
    {
        try
        {
            SetStatus("Loading EdgeGateway Manager.");
            bool authenticated = await EnsureAuthenticatedAsync();
            if (!authenticated)
            {
                Close();
                return;
            }

            await LoadAccessContextAsync();
            if (Can(EdgeGatewayPermission.EditDeviceConfig))
            {
                await ReloadConfigurationAsync();
                await LoadProfilePresetsFromServiceAsync();
            }
            else
            {
                configuration = null;
                PopulateSelectedDeviceSettings();
                ValidationTextBlock.Text = "Viewer role: device config editing is locked.";
            }

            ApplyPermissionState();
            SetRefreshInterval();
            refreshTimer.Start();
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            EnterDisconnectedState(ex);
        }
    }

    private void EnterDisconnectedState(Exception ex)
    {
        accessContext = null;
        configuration = null;
        devices.Clear();
        dashboardRows.Clear();
        deviceLogFiles.Clear();
        exceptionFiles.Clear();
        capturedFiles.Clear();
        preparedOrders.Clear();
        packetLogRows.Clear();
        outputQueueRows.Clear();

        ServiceStatusTextBlock.Text = "unavailable";
        ServiceStatusTextBlock.Foreground = ErrorBrush;
        DashboardServiceTextBlock.Text = "Unavailable";
        DashboardDeviceCountTextBlock.Text = "0";
        DashboardOutputQueueTextBlock.Text = "0";
        DashboardOrderQueueTextBlock.Text = "0";
        DashboardErroredTextBlock.Text = "!";
        AccessRoleTextBlock.Text = "Role: unavailable";
        ValidationTextBlock.Text =
            "EdgeGateway service is not reachable. Start the Windows service from Server Manager, then refresh.";
        SetStatus($"Cannot reach EdgeGateway service: {ex.Message}");

        ApplyPermissionState();
        SetRefreshInterval();
        refreshTimer.Start();
    }

    private async Task<bool> EnsureAuthenticatedAsync()
    {
        EdgeGatewayAuthStatus status = await apiClient.GetAuthStatusAsync();
        if (!status.AuthRequired)
        {
            return true;
        }

        try
        {
            EdgeGatewayAccessContext existingAccess = await apiClient.GetCurrentAccessAsync();
            if (!string.Equals(
                existingAccess.UserName,
                "anonymous",
                StringComparison.OrdinalIgnoreCase
            ))
            {
                return true;
            }
        }
        catch
        {
            // Fall through to the login dialog.
        }

        LoginWindow loginWindow = new(apiClient) { Owner = this };
        return loginWindow.ShowDialog() == true;
    }

    private async Task LoadAccessContextAsync()
    {
        try
        {
            accessContext = await apiClient.GetCurrentAccessAsync();
            AccessRoleTextBlock.Text =
                $"Role: {accessContext.Role} ({accessContext.UserName})";
        }
        catch (Exception ex)
        {
            accessContext = null;
            AccessRoleTextBlock.Text = "Role: unknown";
            SetStatus($"Could not load operator role: {ex.Message}");
        }
    }

    private async Task RefreshAllAsync()
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        try
        {
            EdgeGatewayHealthSummary health = await apiClient.GetHealthAsync();
            ServiceStatusTextBlock.Text = $"is {health.ServiceStatus.ToLowerInvariant()}";
            ServiceStatusTextBlock.Foreground = health.ServiceStatus == "Running"
                ? SuccessBrush
                : ErrorBrush;
            DashboardServiceTextBlock.Text = health.ServiceStatus;
            DashboardDeviceCountTextBlock.Text = health.DeviceCount.ToString();

            UpdateDeviceRows(health.Devices);
            IReadOnlyList<OutputQueueRow> dashboardOutputRows =
                await LoadDashboardOutputRowsAsync();
            IReadOnlyDictionary<string, PreparedOrderQueueSummary> orderSummaries =
                await LoadOrderQueueSummariesAsync();
            RefreshDashboardRows(dashboardOutputRows, orderSummaries);
            await RefreshSelectedDeviceDetailsAsync();
            await RefreshCapturedFilesAsync();
            await RefreshOutputQueueAsync();
            SetStatus($"Status refreshed at {DateTime.Now:HH:mm:ss}.");
        }
        catch (Exception ex)
        {
            ServiceStatusTextBlock.Text = "unavailable";
            ServiceStatusTextBlock.Foreground = ErrorBrush;
            DashboardServiceTextBlock.Text = "Unavailable";
            DashboardErroredTextBlock.Text = "!";
            SetStatus($"Cannot reach EdgeGateway service: {ex.Message}");
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    private async Task<IReadOnlyList<OutputQueueRow>> LoadDashboardOutputRowsAsync()
    {
        IReadOnlyList<OutputQueueItemSnapshot> items =
            await apiClient.GetOutputQueueAsync(500).ConfigureAwait(false);
        return items.Select(OutputQueueRow.From).ToArray();
    }

    private async Task<IReadOnlyDictionary<string, PreparedOrderQueueSummary>>
        LoadOrderQueueSummariesAsync()
    {
        IReadOnlyList<PreparedOrderQueueSummary> summaries =
            await apiClient.GetOrderQueueSummaryAsync().ConfigureAwait(false);
        return summaries.ToDictionary(
            summary => summary.DeviceId,
            StringComparer.OrdinalIgnoreCase
        );
    }

    private void RefreshDashboardRows(
        IReadOnlyList<OutputQueueRow> outputRows,
        IReadOnlyDictionary<string, PreparedOrderQueueSummary> orderSummaries
    )
    {
        dashboardRows.Clear();
        foreach (DeviceViewModel device in devices)
        {
            dashboardRows.Add(DashboardDeviceRow.From(device, outputRows, orderSummaries));
        }

        DashboardOutputQueueTextBlock.Text = dashboardRows
            .Sum(row => row.OutputQueuedCount)
            .ToString();
        DashboardOrderQueueTextBlock.Text = dashboardRows
            .Sum(row => row.OrderQueuedCount)
            .ToString();
        DashboardErroredTextBlock.Text = dashboardRows
            .Sum(row => row.ErroredCount)
            .ToString();
    }

    private void UpdateDeviceRows(IReadOnlyList<DeviceHealthSummary> healthDevices)
    {
        string? selectedDeviceId = SelectedDeviceId();
        devices.Clear();
        IReadOnlyDictionary<string, DeviceHealthSummary> healthByDeviceId = healthDevices
            .ToDictionary(device => device.DeviceId, StringComparer.OrdinalIgnoreCase);
        IEnumerable<DeviceProfile> configuredProfiles = configuration?.Devices ?? [];
        foreach (DeviceProfile profile in configuredProfiles)
        {
            devices.Add(
                healthByDeviceId.TryGetValue(profile.DeviceId, out DeviceHealthSummary? health)
                    ? DeviceViewModel.From(health, profile)
                    : DeviceViewModel.FromProfile(profile)
            );
        }

        foreach (DeviceHealthSummary device in healthDevices)
        {
            if (!devices.Any(row =>
                string.Equals(row.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase)
            ))
            {
                devices.Add(DeviceViewModel.From(device, profile: null));
            }
        }

        DeviceViewModel? selected = selectedDeviceId is null
            ? devices.FirstOrDefault()
            : devices.FirstOrDefault(device =>
                string.Equals(device.DeviceId, selectedDeviceId, StringComparison.OrdinalIgnoreCase)
            );
        DevicesGrid.SelectedItem = selected;
        if (!editMode)
        {
            PopulateSelectedDeviceSettings();
        }
    }

    private async Task RefreshSelectedDeviceDetailsAsync()
    {
        string? deviceId = SelectedDeviceId();
        deviceLogFiles.Clear();
        exceptionFiles.Clear();
        preparedOrders.Clear();
        packetLogRows.Clear();
        DeviceLogDetailsTextBox.Text = "";
        ExceptionDetailsTextBox.Text = "";
        PacketLogDetailsTextBox.Text = "";
        await RefreshLogFilesAsync();
        if (deviceId is null)
        {
            return;
        }

        IReadOnlyList<CommunicationLogEntry> nextExceptions =
            await apiClient.GetDeviceExceptionsAsync(deviceId);

        foreach (LogFileViewModel exceptionFile in LogFileViewModel.FromEntries(
            nextExceptions,
            "exception"
        ))
        {
            exceptionFiles.Add(exceptionFile);
        }

        DeviceLogFilesListBox.SelectedIndex = deviceLogFiles.Count > 0 ? 0 : -1;
        ExceptionFilesListBox.SelectedIndex = exceptionFiles.Count > 0 ? 0 : -1;
        await RefreshOrdersAsync(deviceId);
        await RefreshPacketsAsync(deviceId);
    }

    private async Task RefreshOrdersAsync(string deviceId)
    {
        preparedOrders.Clear();
        IReadOnlyList<PreparedOrder> orders = await apiClient.GetPreparedOrdersAsync(deviceId);
        foreach (PreparedOrder order in orders)
        {
            preparedOrders.Add(PreparedOrderRow.From(order));
        }

        OrdersGrid.SelectedIndex = preparedOrders.Count > 0 ? 0 : -1;
        PopulateSelectedOrderEditor();
    }

    private async Task RefreshPacketsAsync(string deviceId)
    {
        packetLogRows.Clear();
        PacketLogDetailsTextBox.Text = "";
        IReadOnlyList<PacketCommunicationRecord> records =
            await apiClient.GetPacketLogAsync(deviceId, SelectedPacketLimit());
        string filter = PacketDirectionComboBox.SelectedItem?.ToString() ?? "All";
        foreach (PacketCommunicationRecord record in records)
        {
            if (filter != "All"
                && !string.Equals(record.Direction.ToString(), filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            packetLogRows.Add(PacketLogRow.From(record));
        }

        PacketLogGrid.SelectedIndex = packetLogRows.Count > 0 ? 0 : -1;
    }

    private async Task RefreshLogFilesAsync()
    {
        deviceLogFiles.Clear();
        DeviceLogDetailsTextBox.Text = "";
        string category = SelectedLogCategory();
        string level = SelectedLogLevel();
        int limit = SelectedLogLimit();
        IReadOnlyList<CommunicationLogEntry> nextLogs =
            await apiClient.GetLogsByCategoryAsync(category, level, limit);
        DashboardLogTextBox.Text = FormatDashboardLogInformation(nextLogs);

        foreach (LogFileViewModel logFile in LogFileViewModel.FromEntries(
            nextLogs,
            category.ToLowerInvariant()
        ))
        {
            deviceLogFiles.Add(logFile);
        }

        DeviceLogFilesListBox.SelectedIndex = deviceLogFiles.Count > 0 ? 0 : -1;
    }

    private static string FormatDashboardLogInformation(
        IReadOnlyList<CommunicationLogEntry> entries
    )
    {
        if (entries.Count == 0)
        {
            return "No log entries in the selected category.";
        }

        return string.Join(
            Environment.NewLine,
            entries
                .OrderBy(entry => entry.Timestamp)
                .Select(entry =>
                    $"{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  "
                    + $"{CommunicationLogClassifier.GetLevel(entry.EventType),-5}  "
                    + $"{entry.DeviceId,-18}  {entry.EventType,-24}  "
                    + $"{entry.Message}{FormatDashboardError(entry)}"
                )
        );
    }

    private static string FormatDashboardError(CommunicationLogEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.Error) ? "" : $"  {entry.Error}";
    }

    private async Task RefreshCapturedFilesAsync()
    {
        capturedFiles.Clear();
        CapturedFileDetailsTextBox.Text = "";
        IReadOnlyList<CapturedFileSummary> files = await apiClient.GetCapturedFilesAsync();
        foreach (CapturedFileSummary file in files)
        {
            capturedFiles.Add(file);
        }

        CapturedFilesListBox.SelectedIndex = capturedFiles.Count > 0 ? 0 : -1;
    }

    private async Task RefreshOutputQueueAsync()
    {
        outputQueueRows.Clear();
        OutputQueueDetailsTextBox.Text = "";
        string filter = OutputStatusFilterComboBox.SelectedItem?.ToString() ?? "All";
        IReadOnlyList<OutputQueueItemSnapshot> items = string.Equals(
            filter,
            "Dead Letter",
            StringComparison.OrdinalIgnoreCase
        )
            ? await apiClient.GetDeadLetterOutputAsync(SelectedOutputLimit())
            : await apiClient.GetOutputQueueAsync(SelectedOutputLimit());

        foreach (OutputQueueItemSnapshot item in items)
        {
            OutputQueueRow row = OutputQueueRow.From(item);
            if (!OutputStatusMatchesFilter(row, filter))
            {
                continue;
            }

            outputQueueRows.Add(row);
        }

        OutputQueueGrid.SelectedIndex = outputQueueRows.Count > 0 ? 0 : -1;
    }

    private async Task ReloadConfigurationAsync()
    {
        if (!Can(EdgeGatewayPermission.EditDeviceConfig))
        {
            configuration = null;
            PopulateSelectedDeviceSettings();
            ValidationTextBlock.Text = "Viewer role: device config editing is locked.";
            return;
        }

        try
        {
            configuration = await apiClient.GetDeviceConfigurationAsync();
            PopulateSelectedDeviceSettings();
            SetEditMode(false);
            ValidationTextBlock.Text = "";
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"Config unavailable: {ex.Message}";
        }
    }

    private void PopulateSelectedDeviceSettings()
    {
        DeviceProfile? profile = SelectedProfile();
        if (profile is null)
        {
            DeviceNameTextBox.Text = "";
            EnabledCheckBox.IsChecked = false;
            AutoConnectCheckBox.IsChecked = false;
            ProbeEnabledCheckBox.IsChecked = false;
            HostTextBox.Text = "";
            PortTextBox.Text = "";
            ComPortTextBox.Text = "";
            BaudRateTextBox.Text = "9600";
            DataBitsTextBox.Text = "8";
            ParityComboBox.SelectedItem = SerialPortParity.None;
            StopBitsComboBox.SelectedItem = SerialPortStopBits.One;
            FlowControlComboBox.SelectedItem = SerialPortFlowControl.None;
            ReadTimeoutTextBox.Text = "200";
            WriteTimeoutTextBox.Text = "2000";
            ReadCoalesceTextBox.Text = "15";
            MaxBufferTextBox.Text = "1048576";
            RawBoundaryModeComboBox.SelectedItem = RawBoundaryMode.RawChunk;
            RawDelimiterComboBox.SelectedItem = "CRLF";
            RawCustomDelimiterTextBox.Text = "";
            RawFixedLengthTextBox.Text = "";
            RawStartBytesTextBox.Text = "";
            RawEndBytesTextBox.Text = "";
            RawIncludeBoundaryCheckBox.IsChecked = false;
            RawMaxMessageBytesTextBox.Text = "65536";
            AstmDirectionComboBox.SelectedItem = AstmSessionDirection.ReceiveWithAck;
            AstmChecksumModeComboBox.SelectedItem = AstmChecksumMode.Strict;
            AstmFrameNumberModeComboBox.SelectedItem = AstmFrameNumberMode.Warn;
            AstmDuplicateFramePolicyComboBox.SelectedItem =
                AstmDuplicateFramePolicy.DiscardDuplicate;
            AstmEnqCollisionPolicyComboBox.SelectedItem = AstmEnqCollisionPolicy.AnalyzerWins;
            AstmAckEnqCheckBox.IsChecked = true;
            AstmAckFrameCheckBox.IsChecked = true;
            AstmNakChecksumCheckBox.IsChecked = true;
            AstmAckTimeoutTextBox.Text = "3000";
            AstmMaxRetriesTextBox.Text = "3";
            AstmInterFrameTimeoutTextBox.Text = "5000";
            AstmMaxTransactionBytesTextBox.Text = "1048576";
            AstmMaxFrameCountTextBox.Text = "256";
            AstmMaxOutboundPayloadTextBox.Text = "240";
            AstmEndOnEotCheckBox.IsChecked = true;
            AstmOrderReplyEnabledCheckBox.IsChecked = false;
            AstmAutoReplyCheckBox.IsChecked = false;
            AstmUnknownQueryPolicyComboBox.SelectedItem = AstmUnknownQueryPolicy.LogOnly;
            AstmOrderHostTextBox.Text = "EdgeGateway";
            FolderTextBox.Text = "";
            OutputTypeComboBox.SelectedItem = OutputTargetType.Http;
            OutputUrlTextBox.Text = "";
            SelectedBranchTextBlock.Text = "Select a device to view its connection branch.";
            UpdateTransportPanels();
            UpdateProtocolSummary();
            SetEditMode(false);
            return;
        }

        DeviceNameTextBox.Text = profile.Name;
        EnabledCheckBox.IsChecked = profile.Enabled;
        AutoConnectCheckBox.IsChecked = profile.AutoConnect;
        ProbeEnabledCheckBox.IsChecked = profile.ProbeEnabled;
        TransportComboBox.SelectedItem = profile.Transport;
        ProtocolComboBox.SelectedItem = profile.Protocol;
        HostTextBox.Text = profile.Host ?? "";
        PortTextBox.Text = profile.Port?.ToString() ?? "";
        ComPortTextBox.Text = profile.ComPort ?? "";
        BaudRateTextBox.Text = profile.BaudRate.ToString();
        DataBitsTextBox.Text = profile.DataBits.ToString();
        ParityComboBox.SelectedItem = profile.Parity;
        StopBitsComboBox.SelectedItem = profile.StopBits;
        FlowControlComboBox.SelectedItem = profile.FlowControl;
        ReadTimeoutTextBox.Text = profile.ReadTimeoutMs.ToString();
        WriteTimeoutTextBox.Text = profile.WriteTimeoutMs.ToString();
        ReadCoalesceTextBox.Text = profile.ReadCoalesceMs.ToString();
        MaxBufferTextBox.Text = profile.MaxBufferBytes.ToString();
        RawBoundaryModeComboBox.SelectedItem = profile.RawBoundary.Mode;
        RawDelimiterComboBox.SelectedItem = profile.RawBoundary.Delimiter;
        RawCustomDelimiterTextBox.Text = profile.RawBoundary.CustomDelimiter ?? "";
        RawFixedLengthTextBox.Text = profile.RawBoundary.FixedLength?.ToString() ?? "";
        RawStartBytesTextBox.Text = profile.RawBoundary.StartBytes ?? "";
        RawEndBytesTextBox.Text = profile.RawBoundary.EndBytes ?? "";
        RawIncludeBoundaryCheckBox.IsChecked = profile.RawBoundary.IncludeBoundary;
        RawMaxMessageBytesTextBox.Text = profile.RawBoundary.MaxMessageBytes.ToString();
        AstmDirectionComboBox.SelectedItem = profile.Astm.Direction;
        AstmChecksumModeComboBox.SelectedItem = profile.Astm.ChecksumMode;
        AstmFrameNumberModeComboBox.SelectedItem = profile.Astm.FrameNumberMode;
        AstmDuplicateFramePolicyComboBox.SelectedItem = profile.Astm.DuplicateFramePolicy;
        AstmEnqCollisionPolicyComboBox.SelectedItem = profile.Astm.EnqCollisionPolicy;
        AstmAckEnqCheckBox.IsChecked = profile.Astm.AckEnq;
        AstmAckFrameCheckBox.IsChecked = profile.Astm.AckValidFrame;
        AstmNakChecksumCheckBox.IsChecked = profile.Astm.NakBadChecksum;
        AstmAckTimeoutTextBox.Text = profile.Astm.AckTimeoutMs.ToString();
        AstmMaxRetriesTextBox.Text = profile.Astm.MaxFrameRetries.ToString();
        AstmInterFrameTimeoutTextBox.Text = profile.Astm.InterFrameTimeoutMs.ToString();
        AstmMaxTransactionBytesTextBox.Text = profile.Astm.MaxTransactionBytes.ToString();
        AstmMaxFrameCountTextBox.Text = profile.Astm.MaxFrameCount.ToString();
        AstmMaxOutboundPayloadTextBox.Text = profile.Astm.MaxOutboundFramePayloadBytes.ToString();
        AstmEndOnEotCheckBox.IsChecked = profile.Astm.EndMessageOnEot;
        AstmOrderReplyEnabledCheckBox.IsChecked = profile.AstmOrderReply.Enabled;
        AstmAutoReplyCheckBox.IsChecked = profile.AstmOrderReply.AutoReplyMatchedQuery;
        AstmUnknownQueryPolicyComboBox.SelectedItem = profile.AstmOrderReply.UnknownQueryPolicy;
        AstmOrderHostTextBox.Text = profile.AstmOrderReply.Host;
        FolderTextBox.Text = profile.Folder ?? "";
        OutputTypeComboBox.SelectedItem = profile.Output?.Type ?? OutputTargetType.Http;
        OutputUrlTextBox.Text = FormatOutputTargetText(profile);
        SelectedBranchTextBlock.Text = BuildSelectedBranchText(profile);
        UpdateTransportPanels();
        UpdateProtocolSummary();
        SetEditMode(false);
    }

    private async void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetEditMode(false);
        PopulateSelectedDeviceSettings();
        try
        {
            await RefreshSelectedDeviceDetailsAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not refresh selected device details: {ex.Message}");
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        SetRefreshInterval();
        await RefreshAllAsync();
    }

    private async void ExportConfigBundle_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "export config"))
        {
            return;
        }

        await RunCommandAsync(
            "Exporting config bundle.",
            async () =>
            {
                ConfigExportBundle bundle = await apiClient.ExportConfigBundleAsync();
                SaveFileDialog dialog = new()
                {
                    Title = "Export EdgeGateway config bundle",
                    Filter = "EdgeGateway config bundle (*.json)|*.json",
                    FileName = $"edgegateway-config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                    AddExtension = true,
                    DefaultExt = ".json",
                    OverwritePrompt = true,
                };
                if (dialog.ShowDialog(this) != true)
                {
                    SetStatus("Config export cancelled.");
                    return;
                }

                string json = JsonSerializer.Serialize(
                    bundle,
                    DeviceConfigurationJson.CreateSerializerOptions(writeIndented: true)
                );
                await File.WriteAllTextAsync(dialog.FileName, json);
                SetStatus($"Config bundle exported: {dialog.FileName}");
            }
        );
    }

    private async void ImportConfigBundle_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "import config"))
        {
            return;
        }

        await RunCommandAsync(
            "Importing config bundle.",
            async () =>
            {
                OpenFileDialog dialog = new()
                {
                    Title = "Import EdgeGateway config bundle",
                    Filter = "EdgeGateway config bundle (*.json)|*.json",
                    CheckFileExists = true,
                    Multiselect = false,
                };
                if (dialog.ShowDialog(this) != true)
                {
                    SetStatus("Config import cancelled.");
                    return;
                }

                string json = await File.ReadAllTextAsync(dialog.FileName);
                ConfigExportBundle bundle =
                    JsonSerializer.Deserialize<ConfigExportBundle>(
                        json,
                        DeviceConfigurationJson.CreateSerializerOptions()
                    )
                    ?? throw new InvalidOperationException("Config bundle JSON is empty or invalid.");
                ConfigImportPreview preview =
                    await apiClient.ValidateConfigImportBundleAsync(bundle);
                MessageBoxResult decision = MessageBox.Show(
                    this,
                    $"Import {preview.DeviceCount} device profile(s) and {preview.ProfileCount} communication profile file(s)?\n\nCurrent config will be backed up before writing. Runtime reload is required.",
                    "Confirm Config Import",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning
                );
                if (decision != MessageBoxResult.OK)
                {
                    SetStatus("Config import cancelled after validation.");
                    return;
                }

                DeviceConfigurationSaveResult result =
                    await apiClient.ImportConfigBundleAsync(bundle);
                ValidationTextBlock.Text = result.Message;
                await ReloadConfigurationAsync();
                await RefreshAllAsync();
                await PromptRuntimeReloadIfRequiredAsync(result);
                SetStatus("Config bundle imported.");
            }
        );
    }

    private async void RestoreConfigBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.RestoreConfigBackup, "restore config backup"))
        {
            return;
        }

        await RunCommandAsync(
            "Loading config backups.",
            async () =>
            {
                IReadOnlyList<ConfigBackupSummary> backups =
                    await apiClient.GetConfigBackupsAsync();
                if (backups.Count == 0)
                {
                    SetStatus("No config backups found.");
                    return;
                }

                ConfigBackupSummary? selected = ShowBackupPicker(backups);
                if (selected is null)
                {
                    SetStatus("Restore backup cancelled.");
                    return;
                }

                MessageBoxResult decision = MessageBox.Show(
                    this,
                    $"Restore '{selected.FileName}'?\n\nCurrent config will be backed up before restore. Runtime reload is required.",
                    "Confirm Config Restore",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning
                );
                if (decision != MessageBoxResult.OK)
                {
                    SetStatus("Restore backup cancelled.");
                    return;
                }

                DeviceConfigurationSaveResult result =
                    await apiClient.RestoreConfigBackupAsync(selected.FileName);
                ValidationTextBlock.Text = result.Message;
                await ReloadConfigurationAsync();
                await RefreshAllAsync();
                await PromptRuntimeReloadIfRequiredAsync(result);
                SetStatus($"Config restored from backup '{selected.FileName}'.");
            }
        );
    }

    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ManageService, "start service"))
        {
            return;
        }

        await RunCommandAsync(
            "Starting runtime capture.",
            async () =>
            {
                await apiClient.StartServiceAsync();
                await RefreshAllAsync();
            }
        );
    }

    private async void StopService_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ManageService, "stop service"))
        {
            return;
        }

        await RunCommandAsync(
            "Stopping runtime capture.",
            async () =>
            {
                await apiClient.StopServiceAsync();
                await RefreshAllAsync();
            }
        );
    }

    private async void ReloadRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "reload runtime"))
        {
            return;
        }

        await RunCommandAsync(
            "Reloading runtime from saved config.",
            async () =>
            {
                await ReloadRuntimeCoreAsync();
                SetStatus("Runtime reloaded from saved config.");
            }
        );
    }

    private async Task PromptRuntimeReloadIfRequiredAsync(DeviceConfigurationSaveResult result)
    {
        if (!result.ReloadRequired)
        {
            return;
        }

        MessageBoxResult decision = MessageBox.Show(
            this,
            "Saved config requires runtime reload before connection changes take effect.\n\nReload runtime now?",
            "Reload Runtime",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question
        );
        if (decision != MessageBoxResult.OK)
        {
            SetStatus("Config saved. Runtime reload is still required.");
            return;
        }

        await ReloadRuntimeCoreAsync();
        SetStatus("Runtime reloaded from saved config.");
    }

    private async Task ReloadRuntimeCoreAsync()
    {
        await apiClient.ReloadServiceAsync();
        await ReloadConfigurationAsync();
        await RefreshAllAsync();
    }

    private ConfigBackupSummary? ShowBackupPicker(IReadOnlyList<ConfigBackupSummary> backups)
    {
        ListBox listBox = new()
        {
            ItemsSource = backups,
            DisplayMemberPath = nameof(ConfigBackupSummary.FileName),
            SelectionMode = SelectionMode.Single,
            Margin = new Thickness(8),
        };
        listBox.SelectedIndex = 0;

        Button restoreButton = new()
        {
            Content = "Restore",
            Width = 88,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        Button cancelButton = new()
        {
            Content = "Cancel",
            Width = 88,
            IsCancel = true,
        };

        DockPanel root = new();
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8),
        };
        buttons.Children.Add(restoreButton);
        buttons.Children.Add(cancelButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(listBox);

        Window dialog = new()
        {
            Title = "Restore Config Backup",
            Width = 520,
            Height = 320,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root,
        };
        restoreButton.Click += (_, _) => dialog.DialogResult = true;

        return dialog.ShowDialog() == true
            ? listBox.SelectedItem as ConfigBackupSummary
            : null;
    }

    private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ControlDevices, "connect devices"))
        {
            return;
        }

        string? deviceId = SelectedDeviceId();
        if (deviceId is null)
        {
            SetStatus("Select a device first.");
            return;
        }

        await RunCommandAsync(
            $"Connecting {deviceId}.",
            async () =>
            {
                await apiClient.ConnectDeviceAsync(deviceId);
                await RefreshAllAsync();
            }
        );
    }

    private async void DisconnectSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ControlDevices, "disconnect devices"))
        {
            return;
        }

        string? deviceId = SelectedDeviceId();
        if (deviceId is null)
        {
            SetStatus("Select a device first.");
            return;
        }

        await RunCommandAsync(
            $"Disconnecting {deviceId}.",
            async () =>
            {
                await apiClient.DisconnectDeviceAsync(deviceId);
                await RefreshAllAsync();
            }
        );
    }

    private async void ConnectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ControlDevices, "connect all devices"))
        {
            return;
        }

        await RunCommandAsync(
            "Connecting all enabled devices.",
            async () =>
            {
                await apiClient.ConnectAllAsync();
                await RefreshAllAsync();
            }
        );
    }

    private async void DisconnectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ControlDevices, "disconnect all devices"))
        {
            return;
        }

        await RunCommandAsync(
            "Disconnecting all devices.",
            async () =>
            {
                await apiClient.DisconnectAllAsync();
                await RefreshAllAsync();
            }
        );
    }

    private async void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(
            "Reloading config.",
            async () =>
            {
                await ReloadConfigurationAsync();
                await RefreshAllAsync();
            }
        );
    }

    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "save device config"))
        {
            return;
        }

        if (!editMode)
        {
            SetStatus("Click Modify before changing device settings.");
            return;
        }

        await RunCommandAsync(
            "Saving config.",
            async () =>
            {
                DeviceProfile updatedProfile = BuildUpdatedProfile();
                DeviceConfiguration current = configuration
                    ?? throw new InvalidOperationException("Device config is not loaded.");
                DeviceConfiguration next = current with
                {
                    Devices = current.Devices
                        .Select(profile =>
                            string.Equals(
                                profile.DeviceId,
                                updatedProfile.DeviceId,
                                StringComparison.OrdinalIgnoreCase
                            )
                                ? updatedProfile
                                : profile
                        )
                        .ToList(),
                };

                DeviceConfigurationSaveResult result =
                    await apiClient.SaveDeviceConfigurationAsync(next);
                configuration = next;
                ValidationTextBlock.Text = result.Message;
                SetEditMode(false);
                await RefreshAllAsync();
                await PromptRuntimeReloadIfRequiredAsync(result);
            }
        );
    }

    private void Modify_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "modify device config"))
        {
            return;
        }

        if (SelectedProfile() is null)
        {
            SetStatus("Select a device first.");
            return;
        }

        SetEditMode(true);
        ValidationTextBlock.Text = "Edit mode enabled. Save writes a config backup.";
    }

    private async void CancelConfig_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(
            "Cancelling config changes.",
            async () =>
            {
                await ReloadConfigurationAsync();
                SetEditMode(false);
            }
        );
    }

    private async void AddTcpDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "add TCP device"))
        {
            return;
        }

        await CreateDeviceProfileFromPresetAsync("generic/astm-tcp-server-basic");
    }

    private async void AddSerialDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "add serial device"))
        {
            return;
        }

        await CreateDeviceProfileFromPresetAsync("generic/astm-serial-basic");
    }

    private async void AddProfilePresetDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "add device from preset"))
        {
            return;
        }

        if (ProfilePresetComboBox.SelectedValue is not string presetId)
        {
            SetStatus("Select a profile preset first.");
            return;
        }

        await CreateDeviceProfileFromPresetAsync(presetId);
    }

    private async void DeleteDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "delete device profile"))
        {
            return;
        }

        DeviceProfile? profile = SelectedProfile();
        if (profile is null)
        {
            SetStatus("Select a device first.");
            return;
        }

        if (profile.Enabled || profile.AutoConnect)
        {
            ValidationTextBlock.Text =
                "Disable this device and turn off Auto connect, then Save, before deleting it.";
            SetStatus("Device delete is locked until the saved profile is disabled.");
            return;
        }

        MessageBoxResult decision = MessageBox.Show(
            this,
            $"Delete disabled device profile '{profile.Name}'?\n\n"
            + "A config backup will be written first. Raw journals, spool records, packet logs, and output history are not deleted.",
            "Delete Device Profile",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning
        );
        if (decision != MessageBoxResult.OK)
        {
            SetStatus("Device delete cancelled.");
            return;
        }

        await RunCommandAsync(
            $"Deleting device profile '{profile.DeviceId}'.",
            async () =>
            {
                DeviceConfigurationSaveResult result =
                    await apiClient.DeleteDeviceProfileAsync(profile.DeviceId);
                ValidationTextBlock.Text = result.Message;
                SetEditMode(false);
                await ReloadConfigurationAsync();
                await RefreshAllAsync();
                await PromptRuntimeReloadIfRequiredAsync(result);
            }
        );
    }

    private void ExceptionFilesListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        ExceptionDetailsTextBox.Text = DetailForSelectedFiles(ExceptionFilesListBox);
    }

    private void DeviceLogFilesListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        DeviceLogDetailsTextBox.Text = DetailForSelectedFiles(DeviceLogFilesListBox);
    }

    private async void LogCategoryComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            await RefreshLogFilesAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not refresh logs: {ex.Message}");
        }
    }

    private async void LogLevelComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            await RefreshLogFilesAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not refresh logs: {ex.Message}");
        }
    }

    private void CapturedFilesListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        CapturedFileDetailsTextBox.Text = DetailForSelectedCapturedFiles();
    }

    private void OrdersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateSelectedOrderEditor();
    }

    private void NewOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ManagePreparedOrders, "create prepared orders"))
        {
            return;
        }

        OrdersGrid.SelectedItem = null;
        OrderSampleKeyTextBox.Text = "";
        OrderTestCodesTextBox.Text = "";
        OrderEnabledCheckBox.IsChecked = true;
        OrderStatusTextBlock.Text = "New prepared order";
    }

    private async void SaveOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ManagePreparedOrders, "save prepared orders"))
        {
            return;
        }

        string? deviceId = SelectedDeviceId();
        if (deviceId is null)
        {
            SetStatus("Select a device before saving an order.");
            return;
        }

        await RunCommandAsync(
            "Saving prepared order.",
            async () =>
            {
                PreparedOrderRow? selected = OrdersGrid.SelectedItem as PreparedOrderRow;
                PreparedOrderDraft draft = new(
                    selected?.OrderId,
                    deviceId,
                    OrderSampleKeyTextBox.Text.Trim(),
                    ParseTestCodes(OrderTestCodesTextBox.Text),
                    OrderEnabledCheckBox.IsChecked == true
                );
                PreparedOrder order = await apiClient.SavePreparedOrderAsync(deviceId, draft);
                await RefreshOrdersAsync(deviceId);
                OrdersGrid.SelectedItem = preparedOrders.FirstOrDefault(row =>
                    string.Equals(row.OrderId, order.OrderId, StringComparison.OrdinalIgnoreCase)
                );
                SetStatus($"Prepared order saved for sample {order.SampleKey}.");
            }
        );
    }

    private async void DeleteOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ManagePreparedOrders, "delete prepared orders"))
        {
            return;
        }

        string? deviceId = SelectedDeviceId();
        PreparedOrderRow? selected = OrdersGrid.SelectedItem as PreparedOrderRow;
        if (deviceId is null || selected is null)
        {
            SetStatus("Select a prepared order first.");
            return;
        }

        await RunCommandAsync(
            "Deleting prepared order.",
            async () =>
            {
                await apiClient.DeletePreparedOrderAsync(deviceId, selected.OrderId);
                await RefreshOrdersAsync(deviceId);
                SetStatus($"Prepared order {selected.SampleKey} deleted.");
            }
        );
    }

    private async void ReplyOrderNow_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ManagePreparedOrders, "send prepared orders"))
        {
            return;
        }

        string? deviceId = SelectedDeviceId();
        PreparedOrderRow? selected = OrdersGrid.SelectedItem as PreparedOrderRow;
        if (deviceId is null || selected is null)
        {
            SetStatus("Select a prepared order first.");
            return;
        }

        await RunCommandAsync(
            "Sending prepared order test reply.",
            async () =>
            {
                RuntimeCommandResult result =
                    await apiClient.SendPreparedOrderAsync(deviceId, selected.OrderId);
                await RefreshOrdersAsync(deviceId);
                await RefreshPacketsAsync(deviceId);
                SetStatus(result.Message);
            }
        );
    }

    private async void RefreshOrders_Click(object sender, RoutedEventArgs e)
    {
        string? deviceId = SelectedDeviceId();
        if (deviceId is null)
        {
            SetStatus("Select a device first.");
            return;
        }

        await RunCommandAsync("Refreshing prepared orders.", async () => await RefreshOrdersAsync(deviceId));
    }

    private async void RefreshPackets_Click(object sender, RoutedEventArgs e)
    {
        string? deviceId = SelectedDeviceId();
        if (deviceId is null)
        {
            SetStatus("Select a device first.");
            return;
        }

        await RunCommandAsync("Refreshing packet journal.", async () => await RefreshPacketsAsync(deviceId));
    }

    private async void PacketDirectionComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        if (!IsLoaded)
        {
            return;
        }

        string? deviceId = SelectedDeviceId();
        if (deviceId is null)
        {
            return;
        }

        await RunCommandAsync("Filtering packet journal.", async () => await RefreshPacketsAsync(deviceId));
    }

    private void PacketLogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PacketLogDetailsTextBox.Text = (PacketLogGrid.SelectedItem as PacketLogRow)?.Detail ?? "";
    }

    private async void RefreshOutputQueue_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync("Refreshing output queue.", RefreshOutputQueueAsync);
    }

    private async void OutputStatusFilterComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        if (!IsLoaded)
        {
            return;
        }

        await RunCommandAsync("Filtering output queue.", RefreshOutputQueueAsync);
    }

    private void OutputQueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OutputQueueDetailsTextBox.Text =
            (OutputQueueGrid.SelectedItem as OutputQueueRow)?.Detail ?? "";
    }

    private async void ReplaySelectedOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ControlDevices, "replay failed output"))
        {
            return;
        }

        OutputQueueRow? selected = OutputQueueGrid.SelectedItem as OutputQueueRow;
        if (selected is null)
        {
            SetStatus("Select an output item first.");
            return;
        }

        await RunCommandAsync(
            $"Replaying output item {selected.RawId}.",
            async () =>
            {
                OutputReplayResult result =
                    await apiClient.ReplayFailedOutputAsync(selected.RawId);
                await RefreshOutputQueueAsync();
                SetStatus($"Requeued {result.RequeuedCount} output item(s).");
            }
        );
    }

    private async void ReplayAllFailedOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.ControlDevices, "replay failed output"))
        {
            return;
        }

        MessageBoxResult decision = MessageBox.Show(
            this,
            "Replay all failed/dead-letter output items?\n\nUse this after fixing the target or config problem.",
            "Replay Output",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning
        );
        if (decision != MessageBoxResult.OK)
        {
            SetStatus("Output replay cancelled.");
            return;
        }

        await RunCommandAsync(
            "Replaying all failed output items.",
            async () =>
            {
                OutputReplayResult result = await apiClient.ReplayFailedOutputAsync(rawId: null);
                await RefreshOutputQueueAsync();
                SetStatus($"Requeued {result.RequeuedCount} output item(s).");
            }
        );
    }

    private void SelectAllExceptions_Click(object sender, RoutedEventArgs e)
    {
        ExceptionFilesListBox.SelectAll();
        ExceptionDetailsTextBox.Text = DetailForSelectedFiles(ExceptionFilesListBox);
    }

    private void SelectAllDeviceLogs_Click(object sender, RoutedEventArgs e)
    {
        DeviceLogFilesListBox.SelectAll();
        DeviceLogDetailsTextBox.Text = DetailForSelectedFiles(DeviceLogFilesListBox);
    }

    private void SelectAllCapturedFiles_Click(object sender, RoutedEventArgs e)
    {
        CapturedFilesListBox.SelectAll();
        CapturedFileDetailsTextBox.Text = DetailForSelectedCapturedFiles();
    }

    private void DeleteSelectedExceptions_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedLocalFiles(ExceptionFilesListBox, exceptionFiles, ExceptionDetailsTextBox);
    }

    private void DeleteSelectedDeviceLogs_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedLocalFiles(DeviceLogFilesListBox, deviceLogFiles, DeviceLogDetailsTextBox);
    }

    private async void DeleteSelectedCapturedFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.HideCapturedMetadata, "hide captured metadata"))
        {
            return;
        }

        CapturedFileSummary[] selected = CapturedFilesListBox
            .SelectedItems
            .OfType<CapturedFileSummary>()
            .ToArray();

        if (selected.Length == 0)
        {
            SetStatus("No captured file selected.");
            return;
        }

        await RunCommandAsync(
            "Hiding selected captured file metadata.",
            async () =>
            {
                foreach (CapturedFileSummary file in selected)
                {
                    await apiClient.DeleteCapturedFileAsync(file.RawId);
                }

                await RefreshCapturedFilesAsync();
                SetStatus(
                    "Selected captured files hidden from operational view; raw payloads are retained."
                );
            }
        );
    }

    private DeviceProfile BuildUpdatedProfile()
    {
        DeviceProfile profile = SelectedProfile()
            ?? throw new InvalidOperationException("Select a device first.");
        string name = DeviceNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Device name is required.");
        }

        OutputTargetType outputType = OutputTypeComboBox.SelectedItem is OutputTargetType selectedOutputType
            ? selectedOutputType
            : OutputTargetType.Http;
        Uri outputUrl = ParseOutputTargetUri(outputType, OutputUrlTextBox.Text);

        return profile with
        {
            Name = name,
            Enabled = EnabledCheckBox.IsChecked == true,
            AutoConnect = AutoConnectCheckBox.IsChecked == true,
            Transport = TransportComboBox.SelectedItem is DeviceTransport transport
                ? transport
                : profile.Transport,
            Protocol = ProtocolComboBox.SelectedItem is DeviceProtocol protocol
                ? protocol
                : profile.Protocol,
            Host = BlankToNull(HostTextBox.Text),
            Port = ParseNullablePort(PortTextBox.Text),
            ComPort = BlankToNull(ComPortTextBox.Text),
            BaudRate = ParseRequiredInt(BaudRateTextBox.Text, "Baud rate", 300, 921_600),
            DataBits = ParseRequiredInt(DataBitsTextBox.Text, "Data bits", 5, 8),
            Parity = ParityComboBox.SelectedItem is SerialPortParity parity
                ? parity
                : profile.Parity,
            StopBits = StopBitsComboBox.SelectedItem is SerialPortStopBits stopBits
                ? stopBits
                : profile.StopBits,
            FlowControl = FlowControlComboBox.SelectedItem is SerialPortFlowControl flowControl
                ? flowControl
                : profile.FlowControl,
            ReadTimeoutMs = ParseRequiredInt(ReadTimeoutTextBox.Text, "Read timeout", 1, 300_000),
            WriteTimeoutMs = ParseRequiredInt(WriteTimeoutTextBox.Text, "Write timeout", 1, 300_000),
            ReadCoalesceMs = ParseRequiredInt(ReadCoalesceTextBox.Text, "Read coalesce", 0, 5_000),
            MaxBufferBytes = ParseRequiredInt(MaxBufferTextBox.Text, "Max buffer", 1_024, 64 * 1_024 * 1_024),
            RawBoundary = BuildRawBoundaryOptions(profile.RawBoundary),
            Astm = BuildAstmSessionOptions(profile.Astm),
            AstmOrderReply = BuildAstmOrderReplyOptions(profile.AstmOrderReply),
            Folder = BlankToNull(FolderTextBox.Text),
            ProbeEnabled = ProbeEnabledCheckBox.IsChecked == true,
            MirthTargetUrl = null,
            Output = new OutputTargetProfile { Type = outputType, Url = outputUrl },
        };
    }

    private RawBoundaryOptions BuildRawBoundaryOptions(RawBoundaryOptions fallback)
    {
        RawBoundaryMode mode = RawBoundaryModeComboBox.SelectedItem is RawBoundaryMode selectedMode
            ? selectedMode
            : fallback.Mode;
        string delimiter = RawDelimiterComboBox.SelectedItem?.ToString() ?? fallback.Delimiter;

        return fallback with
        {
            Mode = mode,
            Delimiter = string.IsNullOrWhiteSpace(delimiter) ? "CRLF" : delimiter,
            CustomDelimiter = BlankToNull(RawCustomDelimiterTextBox.Text),
            FixedLength = ParseOptionalInt(RawFixedLengthTextBox.Text, "Raw fixed length", 1, 64 * 1_024 * 1_024),
            StartBytes = BlankToNull(RawStartBytesTextBox.Text),
            EndBytes = BlankToNull(RawEndBytesTextBox.Text),
            IncludeBoundary = RawIncludeBoundaryCheckBox.IsChecked == true,
            MaxMessageBytes = ParseRequiredInt(RawMaxMessageBytesTextBox.Text, "Raw max message bytes", 1, 64 * 1_024 * 1_024),
        };
    }

    private AstmSessionOptions BuildAstmSessionOptions(AstmSessionOptions fallback)
    {
        return fallback with
        {
            Direction = AstmDirectionComboBox.SelectedItem is AstmSessionDirection direction
                ? direction
                : fallback.Direction,
            AckEnq = AstmAckEnqCheckBox.IsChecked == true,
            AckValidFrame = AstmAckFrameCheckBox.IsChecked == true,
            NakBadChecksum = AstmNakChecksumCheckBox.IsChecked == true,
            ChecksumMode = AstmChecksumModeComboBox.SelectedItem is AstmChecksumMode checksumMode
                ? checksumMode
                : fallback.ChecksumMode,
            FrameNumberMode = AstmFrameNumberModeComboBox.SelectedItem is AstmFrameNumberMode frameMode
                ? frameMode
                : fallback.FrameNumberMode,
            DuplicateFramePolicy = AstmDuplicateFramePolicyComboBox.SelectedItem is AstmDuplicateFramePolicy duplicatePolicy
                ? duplicatePolicy
                : fallback.DuplicateFramePolicy,
            EnqCollisionPolicy = AstmEnqCollisionPolicyComboBox.SelectedItem is AstmEnqCollisionPolicy collisionPolicy
                ? collisionPolicy
                : fallback.EnqCollisionPolicy,
            EndMessageOnEot = AstmEndOnEotCheckBox.IsChecked == true,
            AckTimeoutMs = ParseRequiredInt(AstmAckTimeoutTextBox.Text, "ASTM ACK timeout", 1, 300_000),
            MaxFrameRetries = ParseRequiredInt(AstmMaxRetriesTextBox.Text, "ASTM max retries", 0, 100),
            InterFrameTimeoutMs = ParseRequiredInt(
                AstmInterFrameTimeoutTextBox.Text,
                "ASTM inter-frame timeout",
                1,
                300_000
            ),
            MaxTransactionBytes = ParseRequiredInt(AstmMaxTransactionBytesTextBox.Text, "ASTM max transaction bytes", 1, 64 * 1_024 * 1_024),
            MaxFrameCount = ParseRequiredInt(AstmMaxFrameCountTextBox.Text, "ASTM max frame count", 1, 10_000),
            MaxOutboundFramePayloadBytes = ParseRequiredInt(AstmMaxOutboundPayloadTextBox.Text, "ASTM outbound frame payload bytes", 1, 8_192),
        };
    }

    private AstmOrderReplyOptions BuildAstmOrderReplyOptions(AstmOrderReplyOptions fallback)
    {
        return fallback with
        {
            Enabled = AstmOrderReplyEnabledCheckBox.IsChecked == true,
            AutoReplyMatchedQuery = AstmAutoReplyCheckBox.IsChecked == true,
            UnknownQueryPolicy = AstmUnknownQueryPolicyComboBox.SelectedItem is AstmUnknownQueryPolicy policy
                ? policy
                : fallback.UnknownQueryPolicy,
            Host = string.IsNullOrWhiteSpace(AstmOrderHostTextBox.Text)
                ? fallback.Host
                : AstmOrderHostTextBox.Text.Trim(),
        };
    }

    private async Task CreateDeviceProfileFromPresetAsync(string presetId)
    {
        if (!RequireUiPermission(EdgeGatewayPermission.EditDeviceConfig, "add device from preset"))
        {
            return;
        }

        MessageBoxResult decision = MessageBox.Show(
            this,
            $"Create a disabled draft device from preset '{presetId}'?\n\n"
            + "After creation, review the connection settings, save changes, then reload runtime before use.",
            "Create Device Draft",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information
        );
        if (decision != MessageBoxResult.OK)
        {
            SetStatus("Device draft creation cancelled.");
            return;
        }

        await RunCommandAsync(
            $"Creating disabled device from preset '{presetId}'.",
            async () =>
            {
                DeviceConfigurationSaveResult result =
                    await apiClient.CreateDeviceFromProfilePresetAsync(presetId);
                ValidationTextBlock.Text = result.Message;
                await ReloadConfigurationAsync();
                await RefreshAllAsync();
                await PromptRuntimeReloadIfRequiredAsync(result);
                SetEditMode(true);
                SetStatus($"Created disabled profile from preset '{presetId}'.");
            }
        );
    }

    private async Task RunCommandAsync(string pendingText, Func<Task> action)
    {
        try
        {
            SetStatus(pendingText);
            await action();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            ValidationTextBlock.Text = ex.Message;
        }
    }

    private void SetRefreshInterval()
    {
        if (!int.TryParse(RefreshSecondsTextBox.Text, out int seconds) || seconds < 1)
        {
            seconds = 5;
            RefreshSecondsTextBox.Text = "5";
        }

        refreshTimer.Interval = TimeSpan.FromSeconds(seconds);
    }

    private void LoadLocalProfilePresets()
    {
        profileLibraryRoot = ResolveProfileLibraryRoot();
        IReadOnlyList<DeviceProfilePreset> presets = profileLibraryRoot is null
            ? DeviceProfilePresetCatalog.All
            : DeviceProfilePresetCatalog.LoadFromLibrary(profileLibraryRoot);
        if (presets.Count == 0)
        {
            profileLibraryRoot = null;
            presets = DeviceProfilePresetCatalog.All;
        }

        SetProfilePresetItems(presets);
    }

    private async Task LoadProfilePresetsFromServiceAsync()
    {
        if (!Can(EdgeGatewayPermission.EditDeviceConfig))
        {
            return;
        }

        try
        {
            IReadOnlyList<DeviceProfilePreset> presets =
                await apiClient.GetProfilePresetsAsync();
            if (presets.Count > 0)
            {
                profileLibraryRoot = null;
                SetProfilePresetItems(presets);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Using local profile presets; service presets unavailable: {ex.Message}");
        }
    }

    private void SetProfilePresetItems(IReadOnlyList<DeviceProfilePreset> presets)
    {
        string? selected = ProfilePresetComboBox.SelectedValue as string;
        ProfilePresetComboBox.ItemsSource = presets;
        ProfilePresetComboBox.DisplayMemberPath = nameof(DeviceProfilePreset.Name);
        ProfilePresetComboBox.SelectedValuePath = nameof(DeviceProfilePreset.Id);
        ProfilePresetComboBox.SelectedValue = selected;
        if (ProfilePresetComboBox.SelectedIndex < 0)
        {
            ProfilePresetComboBox.SelectedIndex = 0;
        }
    }

    private static string? ResolveProfileLibraryRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("EDGE_GATEWAY_PROFILE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (string start in CandidateProfileSearchRoots())
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "profiles");
                if (Directory.Exists(candidate)
                    && Directory.EnumerateFiles(candidate, "*.json", SearchOption.AllDirectories).Any())
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateProfileSearchRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
    }

    private string? SelectedDeviceId()
    {
        return (DevicesGrid.SelectedItem as DeviceViewModel)?.DeviceId;
    }

    private DeviceProfile? SelectedProfile()
    {
        string? deviceId = SelectedDeviceId();
        if (deviceId is null)
        {
            return null;
        }

        return configuration?.Devices.FirstOrDefault(profile =>
            string.Equals(profile.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
        );
    }

    private bool Can(EdgeGatewayPermission permission)
    {
        return accessContext?.Permissions.Contains(permission) == true;
    }

    private bool RequireUiPermission(EdgeGatewayPermission permission, string action)
    {
        if (Can(permission))
        {
            return true;
        }

        string role = accessContext?.Role.ToString() ?? "Unknown";
        SetStatus($"Role {role} is not allowed to {action}.");
        ValidationTextBlock.Text = $"Role {role} is not allowed to {action}.";
        return false;
    }

    private void ApplyPermissionState()
    {
        bool canManageService = Can(EdgeGatewayPermission.ManageService);
        bool canControlDevices = Can(EdgeGatewayPermission.ControlDevices);
        bool canEditConfig = Can(EdgeGatewayPermission.EditDeviceConfig);
        bool canManageOrders = Can(EdgeGatewayPermission.ManagePreparedOrders);
        bool canRestoreConfig = Can(EdgeGatewayPermission.RestoreConfigBackup);
        bool canHideCapturedMetadata = Can(EdgeGatewayPermission.HideCapturedMetadata);

        StartServiceMenuItem.IsEnabled = canManageService;
        StopServiceMenuItem.IsEnabled = canManageService;
        ReloadRuntimeMenuItem.IsEnabled = canEditConfig;
        ExportConfigMenuItem.IsEnabled = canEditConfig;
        ImportConfigMenuItem.IsEnabled = canEditConfig;
        RestoreConfigMenuItem.IsEnabled = canRestoreConfig;
        ConnectAllMenuItem.IsEnabled = canControlDevices;
        DisconnectAllMenuItem.IsEnabled = canControlDevices;

        NewFromPresetButton.IsEnabled = canEditConfig;
        NewTcpButton.IsEnabled = canEditConfig;
        NewSerialButton.IsEnabled = canEditConfig;
        ConnectSelectedButton.IsEnabled = canControlDevices;
        DisconnectSelectedButton.IsEnabled = canControlDevices;
        DeleteDeviceButton.IsEnabled = canEditConfig && !editMode && SelectedProfile() is not null;

        NewOrderButton.IsEnabled = canManageOrders;
        SaveOrderButton.IsEnabled = canManageOrders;
        ReplyOrderButton.IsEnabled = canManageOrders;
        DeleteOrderButton.IsEnabled = canManageOrders;
        OrderSampleKeyTextBox.IsEnabled = canManageOrders;
        OrderTestCodesTextBox.IsEnabled = canManageOrders;
        OrderEnabledCheckBox.IsEnabled = canManageOrders;

        ReplaySelectedOutputButton.IsEnabled = canControlDevices;
        ReplayAllFailedOutputButton.IsEnabled = canControlDevices;

        HideCapturedFilesButton.IsEnabled = canHideCapturedMetadata;
        ModifyConfigButton.IsEnabled =
            canEditConfig && !editMode && SelectedProfile() is not null;
        SaveConfigButton.IsEnabled = canEditConfig && editMode;
        CancelConfigButton.IsEnabled = canEditConfig && editMode;
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private static string DetailForSelectedFiles(ListBox listBox)
    {
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            listBox
                .SelectedItems
                .OfType<LogFileViewModel>()
                .Select(file => file.Detail)
        );
    }

    private string DetailForSelectedCapturedFiles()
    {
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            CapturedFilesListBox
                .SelectedItems
                .OfType<CapturedFileSummary>()
                .Select(file => file.Detail)
        );
    }

    private void PopulateSelectedOrderEditor()
    {
        PreparedOrderRow? selected = OrdersGrid.SelectedItem as PreparedOrderRow;
        if (selected is null)
        {
            OrderSampleKeyTextBox.Text = "";
            OrderTestCodesTextBox.Text = "";
            OrderEnabledCheckBox.IsChecked = true;
            OrderStatusTextBlock.Text = "No prepared order selected";
            return;
        }

        OrderSampleKeyTextBox.Text = selected.SampleKey;
        OrderTestCodesTextBox.Text = selected.TestCodesText;
        OrderEnabledCheckBox.IsChecked = selected.Enabled;
        OrderStatusTextBlock.Text = $"{selected.Status}"
            + (string.IsNullOrWhiteSpace(selected.LastError) ? "" : $" - {selected.LastError}");
    }

    private static string[] ParseTestCodes(string value)
    {
        return value
            .Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(code => code.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void DeleteSelectedLocalFiles(
        ListBox listBox,
        ObservableCollection<LogFileViewModel> files,
        TextBox detailTextBox
    )
    {
        LogFileViewModel[] selected = listBox.SelectedItems.OfType<LogFileViewModel>().ToArray();
        foreach (LogFileViewModel file in selected)
        {
            files.Remove(file);
        }

        detailTextBox.Text = "";
        SetStatus(
            selected.Length == 0
                ? "No log file selected."
                : "Selected log views cleared locally; runtime source log is retained."
        );
    }

    private static string? BlankToNull(string value)
    {
        string trimmed = value.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static int? ParseNullablePort(string value)
    {
        string trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (int.TryParse(trimmed, out int port) && port is >= 1 and <= 65_535)
        {
            return port;
        }

        throw new InvalidOperationException("Port must be between 1 and 65535.");
    }

    private static int ParseRequiredInt(
        string value,
        string label,
        int minimum,
        int maximum
    )
    {
        string trimmed = value.Trim();
        if (int.TryParse(trimmed, out int parsed) && parsed >= minimum && parsed <= maximum)
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"{label} must be between {minimum} and {maximum}."
        );
    }

    private static int? ParseOptionalInt(
        string value,
        string label,
        int minimum,
        int maximum
    )
    {
        string trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return ParseRequiredInt(trimmed, label, minimum, maximum);
    }

    private void TransportComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        UpdateTransportPanels();
    }

    private void ProtocolComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        UpdateProtocolSummary();
    }

    private void RawBoundaryModeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e
    )
    {
        UpdateProtocolSummary();
    }

    private void AstmOrderReplyEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateProtocolSummary();
    }

    private void UpdateTransportPanels()
    {
        DeviceTransport? transport = TransportComboBox.SelectedItem is DeviceTransport selected
            ? selected
            : null;
        bool isTcp = transport is DeviceTransport.TcpClient or DeviceTransport.TcpServer;
        TcpSettingsPanel.Visibility = isTcp ? Visibility.Visible : Visibility.Collapsed;
        SerialSettingsPanel.Visibility = transport == DeviceTransport.Rs232Serial
            ? Visibility.Visible
            : Visibility.Collapsed;
        FolderSettingsPanel.Visibility = transport == DeviceTransport.FolderWatcher
            ? Visibility.Visible
            : Visibility.Collapsed;

        TcpRoleTextBlock.Text = transport switch
        {
            DeviceTransport.TcpServer => "TCP Server: EdgeGateway listens for analyzer.",
            DeviceTransport.TcpClient => "TCP Client: EdgeGateway connects to analyzer.",
            _ => "",
        };

        UpdateProtocolSummary();
    }

    private void UpdateProtocolSummary()
    {
        DeviceProtocol? protocol = ProtocolComboBox.SelectedItem is DeviceProtocol selected
            ? selected
            : null;
        AstmSessionOptions astm = BuildVisibleAstmSummary();

        (string mode, string handshake, string start, string end, string checksum, string direction) =
            protocol switch
            {
                DeviceProtocol.Astm => (
                    "Block",
                    astm.SendsControlResponses ? "ENQ / ACK / NAK / EOT" : "Passive receive",
                    "<STX>",
                    "<ETX/ETB>",
                    astm.ChecksumMode.ToString(),
                    astm.Direction.ToString()
                ),
                DeviceProtocol.Hl7Mllp => (
                    "MLLP",
                    "None",
                    "<VT>",
                    "<FS><CR>",
                    "None",
                    "Inbound"
                ),
                DeviceProtocol.Raw or DeviceProtocol.RawText => (
                    "Raw",
                    "None",
                    BuildRawStartSummary(),
                    BuildRawEndSummary(),
                    "None",
                    RawBoundaryModeComboBox.SelectedItem?.ToString() ?? "Inbound"
                ),
                _ => ("-", "-", "-", "-", "-", "-"),
            };

        FramingModeTextBlock.Text = mode;
        HandshakeTextBlock.Text = handshake;
        FrameStartTextBox.Text = start;
        FrameEndTextBox.Text = end;
        ChecksumTextBlock.Text = checksum;
        DirectionTextBlock.Text = direction;
        AstmPolicyGrid.Visibility = protocol == DeviceProtocol.Astm
            ? Visibility.Visible
            : Visibility.Collapsed;
        RawBoundaryGrid.Visibility = protocol is DeviceProtocol.Raw or DeviceProtocol.RawText
            ? Visibility.Visible
            : Visibility.Collapsed;
        MllpPolicyGrid.Visibility = protocol == DeviceProtocol.Hl7Mllp
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool orderReplyAllowed =
            protocol == DeviceProtocol.Astm
            && TransportComboBox.SelectedItem is DeviceTransport.Rs232Serial;
        AstmOrderReplyEnabledCheckBox.IsEnabled = editMode && orderReplyAllowed;
        AstmAutoReplyCheckBox.IsEnabled =
            editMode && orderReplyAllowed && AstmOrderReplyEnabledCheckBox.IsChecked == true;
        AstmUnknownQueryPolicyComboBox.IsEnabled =
            editMode && orderReplyAllowed && AstmOrderReplyEnabledCheckBox.IsChecked == true;
        AstmOrderHostTextBox.IsEnabled =
            editMode && orderReplyAllowed && AstmOrderReplyEnabledCheckBox.IsChecked == true;
    }

    private string BuildRawStartSummary()
    {
        return RawBoundaryModeComboBox.SelectedItem is RawBoundaryMode mode
            ? mode switch
            {
                RawBoundaryMode.DelimitedText => "-",
                RawBoundaryMode.FixedLength => "length",
                RawBoundaryMode.CustomBytes => BlankToNull(RawStartBytesTextBox.Text) ?? "<STX>",
                RawBoundaryMode.RawChunk => "-",
                _ => "-",
            }
            : "-";
    }

    private string BuildRawEndSummary()
    {
        return RawBoundaryModeComboBox.SelectedItem is RawBoundaryMode mode
            ? mode switch
            {
                RawBoundaryMode.DelimitedText =>
                    RawDelimiterComboBox.SelectedItem?.ToString() ?? "CRLF",
                RawBoundaryMode.FixedLength => BlankToNull(RawFixedLengthTextBox.Text) ?? "fixed length",
                RawBoundaryMode.CustomBytes => BlankToNull(RawEndBytesTextBox.Text) ?? "<ETX>",
                RawBoundaryMode.RawChunk => "read chunk",
                _ => "-",
            }
            : "-";
    }

    private AstmSessionOptions BuildVisibleAstmSummary()
    {
        AstmSessionOptions fallback = SelectedProfile()?.Astm ?? new AstmSessionOptions();
        return fallback with
        {
            Direction = AstmDirectionComboBox.SelectedItem is AstmSessionDirection direction
                ? direction
                : fallback.Direction,
            ChecksumMode = AstmChecksumModeComboBox.SelectedItem is AstmChecksumMode checksumMode
                ? checksumMode
                : fallback.ChecksumMode,
        };
    }

    private void SetEditMode(bool enabled)
    {
        editMode =
            enabled
            && Can(EdgeGatewayPermission.EditDeviceConfig)
            && SelectedProfile() is not null;

        DeviceNameTextBox.IsEnabled = editMode;
        EnabledCheckBox.IsEnabled = editMode;
        AutoConnectCheckBox.IsEnabled = editMode;
        ProbeEnabledCheckBox.IsEnabled = editMode;
        TransportComboBox.IsEnabled = editMode;
        ProtocolComboBox.IsEnabled = editMode;
        HostTextBox.IsEnabled = editMode;
        PortTextBox.IsEnabled = editMode;
        ComPortTextBox.IsEnabled = editMode;
        BaudRateTextBox.IsEnabled = editMode;
        DataBitsTextBox.IsEnabled = editMode;
        ParityComboBox.IsEnabled = editMode;
        StopBitsComboBox.IsEnabled = editMode;
        FlowControlComboBox.IsEnabled = editMode;
        ReadTimeoutTextBox.IsEnabled = editMode;
        WriteTimeoutTextBox.IsEnabled = editMode;
        ReadCoalesceTextBox.IsEnabled = editMode;
        MaxBufferTextBox.IsEnabled = editMode;
        AstmDirectionComboBox.IsEnabled = editMode;
        AstmChecksumModeComboBox.IsEnabled = editMode;
        AstmFrameNumberModeComboBox.IsEnabled = editMode;
        AstmDuplicateFramePolicyComboBox.IsEnabled = editMode;
        AstmEnqCollisionPolicyComboBox.IsEnabled = editMode;
        AstmAckEnqCheckBox.IsEnabled = editMode;
        AstmAckFrameCheckBox.IsEnabled = editMode;
        AstmNakChecksumCheckBox.IsEnabled = editMode;
        AstmAckTimeoutTextBox.IsEnabled = editMode;
        AstmMaxRetriesTextBox.IsEnabled = editMode;
        AstmInterFrameTimeoutTextBox.IsEnabled = editMode;
        AstmMaxTransactionBytesTextBox.IsEnabled = editMode;
        AstmMaxFrameCountTextBox.IsEnabled = editMode;
        AstmMaxOutboundPayloadTextBox.IsEnabled = editMode;
        AstmEndOnEotCheckBox.IsEnabled = editMode;
        RawBoundaryModeComboBox.IsEnabled = editMode;
        RawDelimiterComboBox.IsEnabled = editMode;
        RawCustomDelimiterTextBox.IsEnabled = editMode;
        RawFixedLengthTextBox.IsEnabled = editMode;
        RawStartBytesTextBox.IsEnabled = editMode;
        RawEndBytesTextBox.IsEnabled = editMode;
        RawIncludeBoundaryCheckBox.IsEnabled = editMode;
        RawMaxMessageBytesTextBox.IsEnabled = editMode;
        FolderTextBox.IsEnabled = editMode;
        OutputTypeComboBox.IsEnabled = editMode;
        OutputUrlTextBox.IsEnabled = editMode;
        UpdateProtocolSummary();

        ApplyPermissionState();

        if (editMode)
        {
            refreshTimer.Stop();
        }
        else if (IsLoaded)
        {
            refreshTimer.Start();
        }
    }

    private static string BuildSelectedBranchText(DeviceProfile profile)
    {
        string transportBranch = profile.Transport switch
        {
            DeviceTransport.TcpServer => $"TCP -> Server -> {profile.EndpointSummary()}",
            DeviceTransport.TcpClient => $"TCP -> Client -> {profile.EndpointSummary()}",
            DeviceTransport.Rs232Serial =>
                $"Serial COM -> {profile.EndpointSummary()} -> {profile.BaudRate}/{profile.DataBits}/{profile.Parity}/{profile.StopBits} -> read {profile.ReadTimeoutMs}ms / write {profile.WriteTimeoutMs}ms / coalesce {profile.ReadCoalesceMs}ms",
            DeviceTransport.FolderWatcher => $"Folder Watch -> {profile.EndpointSummary()}",
            _ => profile.EndpointSummary(),
        };

        return $"Device -> {transportBranch} -> {profile.Protocol} -> {(profile.Output?.Type ?? OutputTargetType.Http)} Output";
    }

    private static string FormatOutputTargetText(DeviceProfile profile)
    {
        Uri? target = profile.OutputTargetUrl;
        if (target is null)
        {
            return "";
        }

        return (profile.Output?.Type ?? OutputTargetType.Http) == OutputTargetType.FileFolder
            ? target.LocalPath
            : target.ToString();
    }

    private static Uri ParseOutputTargetUri(OutputTargetType outputType, string value)
    {
        string trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Output target is required.");
        }

        if (outputType == OutputTargetType.FileFolder)
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? fileUri)
                && fileUri.Scheme == Uri.UriSchemeFile)
            {
                return fileUri;
            }

            string fullPath = Path.GetFullPath(trimmed);
            return new Uri(AppendDirectorySeparator(fullPath));
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not "http" and not "https")
        {
            throw new InvalidOperationException("HTTP output target must be an absolute http or https URL.");
        }

        return uri;
    }

    private static string AppendDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Start EdgeGateway.Service first, then use Device Manager to monitor devices, control capture, and edit safe connection fields.",
            "EdgeGateway Device Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private string SelectedLogCategory()
    {
        return LogCategoryComboBox.SelectedItem?.ToString() ?? "Device";
    }

    private string SelectedLogLevel()
    {
        return LogLevelComboBox.SelectedItem?.ToString() ?? "Info";
    }

    private int SelectedLogLimit()
    {
        if (!int.TryParse(LogLimitTextBox.Text, out int limit))
        {
            return 500;
        }

        return Math.Clamp(limit, 1, 2_000);
    }

    private int SelectedPacketLimit()
    {
        if (!int.TryParse(PacketLimitTextBox.Text, out int limit))
        {
            return 500;
        }

        return Math.Clamp(limit, 1, 2_000);
    }

    private int SelectedOutputLimit()
    {
        if (!int.TryParse(OutputLimitTextBox.Text, out int limit))
        {
            return 500;
        }

        return Math.Clamp(limit, 1, 2_000);
    }

    private static bool OutputStatusMatchesFilter(OutputQueueRow row, string filter)
    {
        return filter switch
        {
            "Pending" => row.Status == OutputStatus.Pending,
            "Retrying" => row.Status == OutputStatus.Retrying,
            "Dead Letter" => row.Status == OutputStatus.DeadLetter,
            "Failed" => row.Status == OutputStatus.Failed,
            "Sent" => row.Status == OutputStatus.Sent,
            _ => true,
        };
    }
}
