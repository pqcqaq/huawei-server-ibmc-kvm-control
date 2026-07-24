using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using IbmcKvm.Core.VirtualMedia;
using IbmcKvm.Desktop.Localization;

namespace IbmcKvm.Desktop.Views;

internal sealed partial class VirtualMediaWindow : Window
{
    private readonly VirtualMediaController controller;
    private readonly CancellationTokenSource lifetime = new();
    private bool floppyMounted;
    private bool opticalMounted;
    private bool busy;

    public VirtualMediaWindow(VirtualMediaController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent();
        this.controller.StatusChanged += Controller_StatusChanged;
        Opened += Window_Opened;
        Closed += Window_Closed;
        UpdateSourceControls();
    }

    private async void Window_Opened(object? sender, EventArgs e)
    {
        LocalizationManager.Apply(this);
        await RefreshDrivesAsync();
        try
        {
            var capability = await controller.QueryCapabilityAsync(lifetime.Token);
            CapabilityText.Text = capability.Available
                ? $"VMM {capability.Port} · PBKDF2 suite {capability.CipherSuite.Algorithm}"
                : "VMM 不可用";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CapabilityText.Text = "VMM 查询失败";
            OperationStatusText.Text = exception.Message;
        }
    }

    private void SourceComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSourceControls();

    private void UpdateSourceControls()
    {
        if (!IsLoaded)
        {
            return;
        }

        var floppyPhysical = GetSource(FloppySourceComboBox) == SourceKind.PhysicalDrive;
        FloppyPathPanel.IsVisible = !floppyPhysical;
        FloppyDriveComboBox.IsVisible = floppyPhysical;
        CreateFloppyImageButton.IsVisible = floppyPhysical;

        var opticalSource = GetSource(OpticalSourceComboBox);
        OpticalPathPanel.IsVisible = opticalSource != SourceKind.PhysicalDrive;
        OpticalDriveComboBox.IsVisible = opticalSource == SourceKind.PhysicalDrive;
        CreateOpticalImageButton.IsVisible = opticalSource == SourceKind.PhysicalDrive;
    }

    private async void BrowseFloppyButton_Click(object? sender, RoutedEventArgs e)
    {
        var file = (await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "选择软盘镜像",
            AllowMultiple = false,
            FileTypeFilter = [new("软盘镜像") { Patterns = ["*.img"] }, FilePickerFileTypes.All],
        })).FirstOrDefault();
        if (file is not null)
        {
            FloppyPathTextBox.Text = file.TryGetLocalPath();
        }
    }

    private async void BrowseOpticalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSource(OpticalSourceComboBox) == SourceKind.Directory)
        {
            var folder = (await StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = "选择映射目录",
                AllowMultiple = false,
            })).FirstOrDefault();
            if (folder is not null)
            {
                OpticalPathTextBox.Text = folder.TryGetLocalPath();
            }

            return;
        }

        var file = (await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "选择光盘镜像",
            AllowMultiple = false,
            FileTypeFilter = [new("光盘镜像") { Patterns = ["*.iso"] }, FilePickerFileTypes.All],
        })).FirstOrDefault();
        if (file is not null)
        {
            OpticalPathTextBox.Text = file.TryGetLocalPath();
        }
    }

    private async void MountFloppyButton_Click(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在挂载软驱", async token =>
        {
            if (GetSource(FloppySourceComboBox) == SourceKind.Image)
            {
                var path = RequireFile(FloppyPathTextBox.Text);
                await controller.MountImageAsync(
                    MediaDeviceKind.Floppy,
                    path,
                    FloppyWriteProtectCheckBox.IsChecked != false,
                    token);
            }
            else
            {
                await controller.MountPhysicalAsync(
                    RequireDrive(FloppyDriveComboBox, MediaDeviceKind.Floppy),
                    FloppyWriteProtectCheckBox.IsChecked != false,
                    token);
            }
        });

    private async void MountOpticalButton_Click(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在挂载光驱", async token =>
        {
            switch (GetSource(OpticalSourceComboBox))
            {
                case SourceKind.Image:
                    await controller.MountImageAsync(MediaDeviceKind.Optical, RequireFile(OpticalPathTextBox.Text), cancellationToken: token);
                    break;
                case SourceKind.Directory:
                    var directory = RequireDirectory(OpticalPathTextBox.Text);
                    OpticalProgressBar.IsVisible = true;
                    await controller.MountDirectoryAsync(directory, new Progress<MediaBuildProgress>(value =>
                    {
                        OpticalProgressBar.Value = value.TotalItems == 0 ? 100 : value.CompletedItems * 100d / value.TotalItems;
                        OperationStatusText.Text = $"正在生成目录光盘 · {Path.GetFileName(value.CurrentPath)}";
                    }), token);
                    break;
                case SourceKind.PhysicalDrive:
                    await controller.MountPhysicalAsync(RequireDrive(OpticalDriveComboBox, MediaDeviceKind.Optical), cancellationToken: token);
                    break;
            }
        });

    private async void EjectFloppyButton_Click(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在弹出软驱", token => controller.EjectAsync(MediaDeviceKind.Floppy, token));

    private async void EjectOpticalButton_Click(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在弹出光驱", token => controller.EjectAsync(MediaDeviceKind.Optical, token));

    private async void CreateFloppyImageButton_Click(object? sender, RoutedEventArgs e) =>
        await CreateImageAsync(RequireDrive(FloppyDriveComboBox, MediaDeviceKind.Floppy), ".img", FloppyProgressBar);

    private async void CreateOpticalImageButton_Click(object? sender, RoutedEventArgs e) =>
        await CreateImageAsync(RequireDrive(OpticalDriveComboBox, MediaDeviceKind.Optical), ".iso", OpticalProgressBar);

    private async Task CreateImageAsync(PhysicalDriveDescriptor source, string extension, ProgressBar progressBar)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "保存介质镜像",
            SuggestedFileName = $"media-{DateTime.Now:yyyyMMdd-HHmmss}{extension}",
            FileTypeChoices = [new("介质镜像") { Patterns = [$"*{extension}"] }],
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunOperationAsync("正在制作镜像", async token =>
        {
            progressBar.IsVisible = true;
            await controller.CreateImageAsync(source, path, new Progress<MediaCopyProgress>(value =>
            {
                progressBar.Value = value.Percentage;
                OperationStatusText.Text = $"正在制作镜像 · {value.Percentage:0}%";
            }), token);
        });
    }

    private async void RefreshDrivesButton_Click(object? sender, RoutedEventArgs e) => await RefreshDrivesAsync();

    private async Task RefreshDrivesAsync() => await RunOperationAsync("正在刷新物理设备", async token =>
    {
        var drives = await Task.Run(controller.EnumeratePhysicalDrives, token);
        FloppyDriveComboBox.ItemsSource = drives.Where(static item => item.DeviceKind == MediaDeviceKind.Floppy).ToArray();
        OpticalDriveComboBox.ItemsSource = drives.Where(static item => item.DeviceKind == MediaDeviceKind.Optical).ToArray();
        FloppyDriveComboBox.SelectedIndex = FloppyDriveComboBox.ItemCount > 0 ? 0 : -1;
        OpticalDriveComboBox.SelectedIndex = OpticalDriveComboBox.ItemCount > 0 ? 0 : -1;
    }, showSuccess: false);

    private async void ReconnectButton_Click(object? sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在重新连接虚拟介质", controller.ReconnectAsync);

    private async void UsbResetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (await MessageDialog.ConfirmAsync(
                this,
                "确认 USB reset",
                "重置远端 USB 虚拟媒体设备？当前挂载的介质可能短暂断开。",
                dangerous: true))
        {
            await RunOperationAsync("正在发送 USB reset", token => controller.ResetUsbAsync(confirmed: true, token));
        }
    }

    private async Task RunOperationAsync(string status, Func<CancellationToken, Task> operation, bool showSuccess = true)
    {
        if (busy)
        {
            return;
        }

        SetBusy(true);
        OperationStatusText.Text = status;
        try
        {
            await operation(lifetime.Token);
            if (showSuccess)
            {
                OperationStatusText.Text = "操作完成";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            OperationStatusText.Text = exception.Message;
            await MessageDialog.ShowAsync(this, "虚拟介质操作失败", exception.Message);
        }
        finally
        {
            FloppyProgressBar.IsVisible = false;
            OpticalProgressBar.IsVisible = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        MountFloppyButton.IsEnabled = !value;
        MountOpticalButton.IsEnabled = !value;
        EjectFloppyButton.IsEnabled = !value && floppyMounted;
        EjectOpticalButton.IsEnabled = !value && opticalMounted;
        RefreshDrivesButton.IsEnabled = !value;
        ReconnectButton.IsEnabled = !value;
        UsbResetButton.IsEnabled = !value;
    }

    private void Controller_StatusChanged(object? sender, VirtualMediaSlotStatus status) => Dispatcher.UIThread.Post(() =>
    {
        var text = status.IsMounted
            ? $"{status.State} · {status.DisplayName} · {(status.IsReadOnly ? "只读" : "可写")}"
            : status.State;
        if (status.DeviceKind == MediaDeviceKind.Floppy)
        {
            floppyMounted = status.IsMounted;
            FloppyStatusText.Text = text;
            MountFloppyButton.Content = status.IsMounted ? "更换" : "挂载";
        }
        else
        {
            opticalMounted = status.IsMounted;
            OpticalStatusText.Text = text;
            MountOpticalButton.Content = status.IsMounted ? "更换" : "挂载";
        }

        SetBusy(busy);
    });

    private void Window_Closed(object? sender, EventArgs e)
    {
        controller.StatusChanged -= Controller_StatusChanged;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private static SourceKind GetSource(ComboBox comboBox) =>
        comboBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<SourceKind>(tag, out var result)
            ? result
            : SourceKind.Image;

    private static PhysicalDriveDescriptor RequireDrive(ComboBox comboBox, MediaDeviceKind kind)
    {
        if (comboBox.SelectedItem is not PhysicalDriveDescriptor drive)
        {
            throw new InvalidOperationException(kind == MediaDeviceKind.Floppy
                ? "未检测到可用的物理软驱。"
                : "未检测到可用的物理光驱。");
        }

        if (!drive.IsReady)
        {
            throw new InvalidOperationException($"{drive.DisplayName} 中没有就绪的介质。");
        }

        return drive;
    }

    private static string RequireFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? path
            : throw new FileNotFoundException("请选择存在的镜像文件。", path);

    private static string RequireDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException("请选择存在的本地目录。");

    private enum SourceKind
    {
        Image,
        Directory,
        PhysicalDrive,
    }
}
