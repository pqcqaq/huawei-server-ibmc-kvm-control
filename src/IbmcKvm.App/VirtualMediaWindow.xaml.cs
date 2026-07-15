using System.IO;
using System.Windows;
using System.Windows.Controls;
using IbmcKvm.App.VirtualMedia;
using IbmcKvm.Core.VirtualMedia;
using Microsoft.Win32;
using IbmcKvm.App.Localization;

namespace IbmcKvm.App;

public partial class VirtualMediaWindow : Window, IDisposable
{
    private readonly VirtualMediaController controller;
    private readonly CancellationTokenSource lifetime = new();
    private bool floppyMounted;
    private bool opticalMounted;
    private bool busy;
    private int disposed;

    public VirtualMediaWindow(VirtualMediaController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent();
        this.controller.StatusChanged += Controller_StatusChanged;
        Loaded += Window_Loaded;
        UpdateSourceControls();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshDrivesAsync();
        try
        {
            var capability = await controller.QueryCapabilityAsync(lifetime.Token);
            CapabilityText.Text = capability.Available
                ? $"VMM {capability.Port} · PBKDF2 suite {capability.CipherSuite.Algorithm}"
                : LocalizationManager.Translate("VMM 不可用");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CapabilityText.Text = LocalizationManager.Translate("VMM 查询失败");
            OperationStatusText.Text = exception.Message;
        }
    }

    private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateSourceControls();
    }

    private void UpdateSourceControls()
    {
        var floppy = VirtualMediaUiRules.GetControlState(MediaDeviceKind.Floppy, GetSource(FloppySourceComboBox));
        FloppyPathPanel.Visibility = ToVisibility(floppy.ShowPath);
        FloppyDriveComboBox.Visibility = ToVisibility(floppy.ShowPhysicalDrive);
        FloppyWriteProtectCheckBox.Visibility = ToVisibility(floppy.ShowWriteProtection);
        CreateFloppyImageButton.Visibility = ToVisibility(floppy.CanCreateImage);

        var optical = VirtualMediaUiRules.GetControlState(MediaDeviceKind.Optical, GetSource(OpticalSourceComboBox));
        OpticalPathPanel.Visibility = ToVisibility(optical.ShowPath);
        OpticalDriveComboBox.Visibility = ToVisibility(optical.ShowPhysicalDrive);
        CreateOpticalImageButton.Visibility = ToVisibility(optical.CanCreateImage);
    }

    private void BrowseFloppyButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationManager.Translate("选择软盘镜像"),
            Filter = LocalizationManager.Translate("软盘镜像 (*.img)|*.img|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            FloppyPathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseOpticalButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSource(OpticalSourceComboBox) == VirtualMediaSourceKind.Directory)
        {
            var dialog = new OpenFolderDialog
            {
                Title = LocalizationManager.Translate("选择映射目录"),
                Multiselect = false,
            };
            if (dialog.ShowDialog(this) == true)
            {
                OpticalPathTextBox.Text = dialog.FolderName;
            }

            return;
        }

        var fileDialog = new OpenFileDialog
        {
            Title = LocalizationManager.Translate("选择光盘镜像"),
            Filter = LocalizationManager.Translate("光盘镜像 (*.iso)|*.iso|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (fileDialog.ShowDialog(this) == true)
        {
            OpticalPathTextBox.Text = fileDialog.FileName;
        }
    }

    private async void MountFloppyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("正在挂载软驱", async cancellationToken =>
        {
            var source = GetSource(FloppySourceComboBox);
            if (source == VirtualMediaSourceKind.Image)
            {
                ValidateExistingFile(FloppyPathTextBox.Text);
                await controller.MountImageAsync(
                    MediaDeviceKind.Floppy,
                    FloppyPathTextBox.Text,
                    FloppyWriteProtectCheckBox.IsChecked != false,
                    cancellationToken);
            }
            else
            {
                var drive = GetSelectedDrive(FloppyDriveComboBox, MediaDeviceKind.Floppy);
                await controller.MountPhysicalAsync(
                    drive,
                    FloppyWriteProtectCheckBox.IsChecked != false,
                    cancellationToken);
            }
        });
    }

    private async void MountOpticalButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("正在挂载光驱", async cancellationToken =>
        {
            switch (GetSource(OpticalSourceComboBox))
            {
                case VirtualMediaSourceKind.Image:
                    ValidateExistingFile(OpticalPathTextBox.Text);
                    await controller.MountImageAsync(
                        MediaDeviceKind.Optical,
                        OpticalPathTextBox.Text,
                        cancellationToken: cancellationToken);
                    break;
                case VirtualMediaSourceKind.Directory:
                    ValidateExistingDirectory(OpticalPathTextBox.Text);
                    OpticalProgressBar.Visibility = Visibility.Visible;
                    var progress = new Progress<MediaBuildProgress>(value =>
                    {
                        OpticalProgressBar.Value = value.TotalItems == 0
                            ? 100
                            : value.CompletedItems * 100d / value.TotalItems;
                        OperationStatusText.Text = LocalizationManager.Format(
                            "正在生成目录光盘 · {0}",
                            Path.GetFileName(value.CurrentPath));
                    });
                    await controller.MountDirectoryAsync(OpticalPathTextBox.Text, progress, cancellationToken);
                    break;
                case VirtualMediaSourceKind.PhysicalDrive:
                    await controller.MountPhysicalAsync(
                        GetSelectedDrive(OpticalDriveComboBox, MediaDeviceKind.Optical),
                        cancellationToken: cancellationToken);
                    break;
            }
        });
    }

    private async void EjectFloppyButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在弹出软驱", token => controller.EjectAsync(MediaDeviceKind.Floppy, token));

    private async void EjectOpticalButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在弹出光驱", token => controller.EjectAsync(MediaDeviceKind.Optical, token));

    private async void CreateFloppyImageButton_Click(object sender, RoutedEventArgs e) =>
        await CreateSelectedImageAsync(
            FloppyDriveComboBox,
            MediaDeviceKind.Floppy,
            LocalizationManager.Translate("软盘镜像 (*.img)|*.img"),
            ".img",
            FloppyProgressBar);

    private async void CreateOpticalImageButton_Click(object sender, RoutedEventArgs e) =>
        await CreateSelectedImageAsync(
            OpticalDriveComboBox,
            MediaDeviceKind.Optical,
            LocalizationManager.Translate("光盘镜像 (*.iso)|*.iso"),
            ".iso",
            OpticalProgressBar);

    private async Task CreateSelectedImageAsync(
        ComboBox comboBox,
        MediaDeviceKind kind,
        string filter,
        string extension,
        ProgressBar progressBar)
    {
        try
        {
            await CreateImageAsync(GetSelectedDrive(comboBox, kind), filter, extension, progressBar);
        }
        catch (Exception exception)
        {
            OperationStatusText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                LocalizationManager.Translate("无法制作镜像"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CreateImageAsync(
        PhysicalDriveDescriptor source,
        string filter,
        string extension,
        ProgressBar progressBar)
    {
        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.Translate("保存介质镜像"),
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"media-{DateTime.Now:yyyyMMdd-HHmmss}{extension}",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunOperationAsync("正在制作镜像", async cancellationToken =>
        {
            progressBar.Visibility = Visibility.Visible;
            var progress = new Progress<MediaCopyProgress>(value =>
            {
                progressBar.Value = value.Percentage;
                OperationStatusText.Text = LocalizationManager.Format("正在制作镜像 · {0:0}%", value.Percentage);
            });
            await controller.CreateImageAsync(source, dialog.FileName, progress, cancellationToken);
        });
    }

    private async void RefreshDrivesButton_Click(object sender, RoutedEventArgs e) => await RefreshDrivesAsync();

    private async Task RefreshDrivesAsync()
    {
        await RunOperationAsync("正在刷新物理驱动器", async cancellationToken =>
        {
            var drives = await Task.Run(controller.EnumeratePhysicalDrives, cancellationToken);
            FloppyDriveComboBox.ItemsSource = drives.Where(static drive => drive.DeviceKind == MediaDeviceKind.Floppy).ToArray();
            OpticalDriveComboBox.ItemsSource = drives.Where(static drive => drive.DeviceKind == MediaDeviceKind.Optical).ToArray();
            if (FloppyDriveComboBox.Items.Count > 0)
            {
                FloppyDriveComboBox.SelectedIndex = 0;
            }

            if (OpticalDriveComboBox.Items.Count > 0)
            {
                OpticalDriveComboBox.SelectedIndex = 0;
            }
        }, showSuccess: false);
    }

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("正在重新连接虚拟媒体", controller.ReconnectAsync);

    private async void UsbResetButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            LocalizationManager.Translate("确认重置远端 USB 虚拟媒体设备？当前挂载的虚拟介质可能会短暂断开。"),
            LocalizationManager.Translate("确认 USB reset"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync("正在发送 USB reset", token => controller.ResetUsbAsync(confirmed: true, token));
    }

    private async Task RunOperationAsync(
        string status,
        Func<CancellationToken, Task> operation,
        bool showSuccess = true)
    {
        if (busy)
        {
            return;
        }

        SetBusy(true);
        OperationStatusText.Text = LocalizationManager.Translate(status);
        try
        {
            await operation(lifetime.Token);
            if (showSuccess)
            {
                OperationStatusText.Text = LocalizationManager.Translate("操作完成");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            OperationStatusText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                LocalizationManager.Translate("虚拟媒体操作失败"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            FloppyProgressBar.Visibility = Visibility.Collapsed;
            OpticalProgressBar.Visibility = Visibility.Collapsed;
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
        ReconnectButton.IsEnabled = !value;
        RefreshDrivesButton.IsEnabled = !value;
        UsbResetButton.IsEnabled = !value;
        CreateFloppyImageButton.IsEnabled = !value;
        CreateOpticalImageButton.IsEnabled = !value;
    }

    private void Controller_StatusChanged(object? sender, VirtualMediaSlotStatus status)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var state = LocalizationManager.Translate(status.State);
            var text = status.IsMounted
                ? LocalizationManager.Format(
                    "{0} · {1} · {2}",
                    state,
                    status.DisplayName,
                    LocalizationManager.Translate(status.IsReadOnly ? "只读" : "可写"))
                : state;
            if (status.DeviceKind == MediaDeviceKind.Floppy)
            {
                floppyMounted = status.IsMounted;
                FloppyStatusText.Text = text;
                MountFloppyButton.Content = LocalizationManager.Translate(status.IsMounted ? "更换" : "挂载");
            }
            else
            {
                opticalMounted = status.IsMounted;
                OpticalStatusText.Text = text;
                MountOpticalButton.Content = LocalizationManager.Translate(status.IsMounted ? "更换" : "挂载");
            }

            SetBusy(busy);
        });
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        controller.StatusChanged -= Controller_StatusChanged;
        lifetime.Cancel();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private static VirtualMediaSourceKind GetSource(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value ||
            !Enum.TryParse<VirtualMediaSourceKind>(value, out var result))
        {
            return VirtualMediaSourceKind.Image;
        }

        return result;
    }

    private static PhysicalDriveDescriptor GetSelectedDrive(ComboBox comboBox, MediaDeviceKind kind)
    {
        if (comboBox.SelectedItem is not PhysicalDriveDescriptor drive)
        {
            throw new InvalidOperationException(LocalizationManager.Translate(kind == MediaDeviceKind.Floppy
                ? "未检测到可用的物理软驱。"
                : "未检测到可用的物理光驱。"));
        }

        if (!drive.IsReady)
        {
            throw new InvalidOperationException(
                LocalizationManager.Format("{0} 中没有就绪的介质。", drive.DisplayName));
        }

        return drive;
    }

    private static void ValidateExistingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException(LocalizationManager.Translate("请选择存在的镜像文件。"), path);
        }
    }

    private static void ValidateExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(LocalizationManager.Translate("请选择存在的本地目录。"));
        }
    }

    private static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
