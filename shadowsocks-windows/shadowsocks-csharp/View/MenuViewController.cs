using NLog;
using Shadowsocks.Controller;
using Shadowsocks.Localization;
using Shadowsocks.Model;
using Shadowsocks.Properties;
using Shadowsocks.Util;
using Shadowsocks.Views;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using System.Threading;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Shadowsocks.View
{
    public class MenuViewController : IDisposable
    {
        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private ShadowsocksController controller;
        public UpdateChecker updateChecker;

        private NotifyIcon _notifyIcon;
        private Icon icon, icon_in, icon_out, icon_both, icon_offline, icon_transparent, previousIcon;
        private System.Windows.Forms.Timer _blinkTimer;
        private bool _isOfflineBlinking;
        private bool _blinkToggle;
        private SynchronizationContext _uiContext;
        private InternetConnectivityMonitor _connectivityMonitor;

        private bool _isStartupCheck;
        private string _urlToOpen;

        private ContextMenu contextMenu1;
        private MenuItem disableItem;
        private MenuItem AutoStartupItem;
        private MenuItem ProtocolHandlerItem;
        private MenuItem ShareOverLANItem;
        private MenuItem SeperatorItem;
        private MenuItem ConfigItem;
        private MenuItem ServersItem;
        private MenuItem globalModeItem;
        private MenuItem PACModeItem;
        private MenuItem localPACItem;
        private MenuItem onlinePACItem;
        private MenuItem editLocalPACItem;
        private MenuItem updateFromGeositeItem;
        private MenuItem editGFWUserRuleItem;
        private MenuItem editOnlinePACItem;
        private MenuItem secureLocalPacUrlToggleItem;
        private MenuItem regenerateLocalPacOnUpdateItem;
        private MenuItem autoCheckUpdatesToggleItem;
        private MenuItem checkPreReleaseToggleItem;
        private MenuItem proxyItem;
        private MenuItem hotKeyItem;
        private MenuItem VerboseLoggingToggleItem;
        private MenuItem ShowPluginOutputToggleItem;
        private MenuItem WriteI18NFileItem;
        private MenuItem onlineConfigItem;

        private ConfigForm configForm;
        private LogForm logForm;

        private System.Windows.Window serverSharingWindow;
        private System.Windows.Window hotkeysWindow;
        private System.Windows.Window forwardProxyWindow;
        private System.Windows.Window onlineConfigWindow;

        // color definition for icon color transformation
        private readonly Color colorMaskBlue = Color.FromArgb(255, 25, 125, 191);
        // 禁用状态：在浅色主题下使用更深且更不透明的灰色以提高可见性
        private readonly Color colorMaskDarkSilver = Color.FromArgb(160, 176, 176, 176);
        // 禁用状态（暗色主题）：使用略深的灰色
        private readonly Color colorMaskLightSilver = Color.FromArgb(176, 176, 176);
        private readonly Color colorMaskEclipse = Color.FromArgb(192, 64, 64, 64);

        public MenuViewController(ShadowsocksController controller)
        {
            this.controller = controller;
            _uiContext = null; // will capture when UI message loop is ready
            _blinkTimer = new System.Windows.Forms.Timer();
            _blinkTimer.Interval = 600;
            _blinkTimer.Tick += BlinkTimer_Tick;

            LoadMenu();

            controller.EnableStatusChanged += controller_EnableStatusChanged;
            controller.ConfigChanged += controller_ConfigChanged;
            controller.PACFileReadyToOpen += controller_FileReadyToOpen;
            controller.UserRuleFileReadyToOpen += controller_FileReadyToOpen;
            controller.ShareOverLANStatusChanged += controller_ShareOverLANStatusChanged;
            controller.VerboseLoggingStatusChanged += controller_VerboseLoggingStatusChanged;
            controller.ShowPluginOutputChanged += controller_ShowPluginOutputChanged;
            controller.EnableGlobalChanged += controller_EnableGlobalChanged;
            controller.Errored += controller_Errored;
            controller.UpdatePACFromGeositeCompleted += controller_UpdatePACFromGeositeCompleted;
            controller.UpdatePACFromGeositeError += controller_UpdatePACFromGeositeError;

            _notifyIcon = new NotifyIcon();
            UpdateTrayIconAndNotifyText();
            _notifyIcon.Visible = true;
            _notifyIcon.ContextMenu = contextMenu1;
            _notifyIcon.BalloonTipClicked += notifyIcon1_BalloonTipClicked;
            _notifyIcon.MouseClick += notifyIcon1_Click;
            _notifyIcon.MouseDoubleClick += notifyIcon1_DoubleClick;
            _notifyIcon.BalloonTipClosed += _notifyIcon_BalloonTipClosed;
            controller.TrafficChanged += controller_TrafficChanged;

            updateChecker = new UpdateChecker();
            updateChecker.CheckUpdateCompleted += updateChecker_CheckUpdateCompleted;

            LoadCurrentConfiguration();

            InitializeConnectivityMonitor();

            Configuration config = controller.GetCurrentConfiguration();

            if (config.firstRun)
            {
                ShowConfigForm();
            }
            else if (config.autoCheckUpdate)
            {
                _isStartupCheck = true;
                Dispatcher.CurrentDispatcher.Invoke(() => updateChecker.CheckForVersionUpdate(3000));
            }
        }

        #region Tray Icon

        /// <summary>
        /// 初始化联网状态监控。
        /// </summary>
        private void InitializeConnectivityMonitor()
        {
            // 创建监控器但延后启动，等待 UI 消息循环就绪以保证闪烁 Timer 在 UI 线程上工作
            _connectivityMonitor = new InternetConnectivityMonitor();
            _connectivityMonitor.ConnectivityChanged += ConnectivityMonitor_ConnectivityChanged;

            // 当应用消息循环启动并进入空闲时，捕获 UI SynchronizationContext 并启动监控
            Application.Idle += StartConnectivityMonitorOnIdle;
        }

        /// <summary>
        /// 在应用进入空闲时捕获 UI SynchronizationContext 并启动联网监控器。
        /// 仅执行一次，然后取消订阅 Idle 事件。
        /// </summary>
        private void StartConnectivityMonitorOnIdle(object sender, EventArgs e)
        {
            try
            {
                Application.Idle -= StartConnectivityMonitorOnIdle;
                if (_uiContext == null)
                {
                    _uiContext = SynchronizationContext.Current ?? new System.Windows.Forms.WindowsFormsSynchronizationContext();
                }
                _connectivityMonitor?.Start();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to start InternetConnectivityMonitor on idle.");
            }
        }

        /// <summary>
        /// 处理联网状态变化事件。
        /// </summary>
        private void ConnectivityMonitor_ConnectivityChanged(object sender, ConnectivityChangedEventArgs e)
        {
            _uiContext.Post(_ =>
            {
                if (e.IsConnected)
                {
                    StopOfflineBlink();
                }
                else
                {
                    StartOfflineBlink();
                }
            }, null);
        }

        private void UpdateTrayIconAndNotifyText()
        {
            Configuration config = controller.GetCurrentConfiguration();
            bool enabled = config.enabled;
            bool global = config.global;

            Color colorMask = SelectColorMask(enabled, global);
            Size iconSize = SelectIconSize();

            UpdateIconSet(colorMask, iconSize, out icon, out icon_in, out icon_out, out icon_both, out icon_offline, out icon_transparent);

            previousIcon = icon;
            if (!_isOfflineBlinking)
            {
                _notifyIcon.Icon = previousIcon;
            }

            string serverInfo = null;
            if (controller.GetCurrentStrategy() != null)
            {
                serverInfo = controller.GetCurrentStrategy().Name;
            }
            else
            {
                serverInfo = config.GetCurrentServer().ToString();
            }
            // show more info by hacking the P/Invoke declaration for NOTIFYICONDATA inside Windows Forms
            string text = I18N.GetString("Shadowsocks") + " " + UpdateChecker.Version + "\n" +
                          (enabled ?
                              I18N.GetString("System Proxy On: ") + (global ? I18N.GetString("Global") : I18N.GetString("PAC")) :
                              I18N.GetString("Running: Port {0}", config.localPort))  // this feedback is very important because they need to know Shadowsocks is running
                          + "\n" + serverInfo;
            if (text.Length > 127)
            {
                text = text.Substring(0, 126 - 3) + "...";
            }
            ViewUtils.SetNotifyIconText(_notifyIcon, text);
        }

        /// <summary>
        /// Determine the icon size based on the screen DPI.
        /// </summary>
        /// <returns></returns>
        /// https://stackoverflow.com/a/40851713/2075611
        private Size SelectIconSize()
        {
            Size size = new Size(32, 32);
            int dpi = ViewUtils.GetScreenDpi();
            if (dpi < 97)
            {
                // dpi = 96;
                size = new Size(16, 16);
            }
            else if (dpi < 121)
            {
                // dpi = 120;
                size = new Size(20, 20);
            }
            else if (dpi < 145)
            {
                // dpi = 144;
                size = new Size(24, 24);
            }
            else
            {
                // dpi = 168;
                size = new Size(28, 28);
            }
            return size;
        }

        private Color SelectColorMask(bool isProxyEnabled, bool isGlobalProxy)
        {
            Color colorMask = Color.White;

            Utils.WindowsThemeMode currentWindowsThemeMode = Utils.GetWindows10SystemThemeSetting();

            if (isProxyEnabled)
            {
                if (isGlobalProxy)  // global
                {
                    colorMask = colorMaskBlue;
                }
                else  // PAC
                {
                    if (currentWindowsThemeMode == Utils.WindowsThemeMode.Light)
                    {
                        colorMask = colorMaskEclipse;
                    }
                }
            }
            else  // disabled
            {
                if (currentWindowsThemeMode == Utils.WindowsThemeMode.Light)
                {
                    colorMask = colorMaskDarkSilver;
                }
                else
                {
                    colorMask = colorMaskLightSilver;
                }
            }

            return colorMask;
        }

        private void UpdateIconSet(Color colorMask, Size size,
            out Icon icon, out Icon icon_in, out Icon icon_out, out Icon icon_both, out Icon icon_offline, out Icon icon_transparent)
        {
            Bitmap iconBitmap;

            // generate the base icon
            iconBitmap = ViewUtils.ChangeBitmapColor(Resources.ss32Fill, colorMask);
            iconBitmap = ViewUtils.AddBitmapOverlay(iconBitmap, Resources.ss32Outline);

            icon = Icon.FromHandle(ViewUtils.ResizeBitmap(iconBitmap, size.Width, size.Height).GetHicon());
            icon_in = Icon.FromHandle(ViewUtils.ResizeBitmap(ViewUtils.AddBitmapOverlay(iconBitmap, Resources.ss32In), size.Width, size.Height).GetHicon());
            icon_out = Icon.FromHandle(ViewUtils.ResizeBitmap(ViewUtils.AddBitmapOverlay(iconBitmap, Resources.ss32In), size.Width, size.Height).GetHicon());
            icon_both = Icon.FromHandle(ViewUtils.ResizeBitmap(ViewUtils.AddBitmapOverlay(iconBitmap, Resources.ss32In, Resources.ss32Out), size.Width, size.Height).GetHicon());

            Bitmap offlineBitmap = ViewUtils.ChangeBitmapColor(Resources.ss32Fill, Color.FromArgb(255, 220, 32, 32));
            offlineBitmap = ViewUtils.AddBitmapOverlay(offlineBitmap, Resources.ss32Outline);
            icon_offline = Icon.FromHandle(ViewUtils.ResizeBitmap(offlineBitmap, size.Width, size.Height).GetHicon());

            // 生成一个完全透明的图标，用于实现“透明闪烁”效果（若希望闪烁为不可见）
            Bitmap transparent = new Bitmap(size.Width, size.Height);
            using (Graphics g = Graphics.FromImage(transparent))
            {
                g.Clear(Color.Transparent);
                g.Save();
            }
            icon_transparent = Icon.FromHandle(ViewUtils.ResizeBitmap(transparent, size.Width, size.Height).GetHicon());
        }

        /// <summary>
        /// 启动托盘图标闪烁以提示网络断开。
        /// </summary>
        private void StartOfflineBlink()
        {
            if (_isOfflineBlinking || (_notifyIcon == null) || (icon_offline == null && icon_transparent == null))
            {
                return;
            }

            _isOfflineBlinking = true;
            _blinkToggle = false;
            // 优先使用透明图标实现“闪烁为透明”的效果；若不可用则退回为红色图标
            if (icon_transparent != null)
            {
                _notifyIcon.Icon = icon_transparent;
            }
            else
            {
                _notifyIcon.Icon = icon_offline;
            }
            _blinkTimer.Start();
        }

        /// <summary>
        /// 停止托盘图标闪烁并恢复原始图标。
        /// </summary>
        private void StopOfflineBlink()
        {
            if (!_isOfflineBlinking || _notifyIcon == null)
            {
                return;
            }

            _blinkTimer.Stop();
            _blinkToggle = false;
            _isOfflineBlinking = false;
            if (previousIcon != null)
            {
                _notifyIcon.Icon = previousIcon;
            }
        }

        /// <summary>
        /// 托盘图标闪烁定时器回调。
        /// </summary>
        private void BlinkTimer_Tick(object sender, EventArgs e)
        {
            if (!_isOfflineBlinking || previousIcon == null || _notifyIcon == null)
            {
                return;
            }

            // 如果存在透明图标，则在透明和原图之间切换；否则在红色离线图标和原图之间切换
            if (icon_transparent == null && icon_offline == null)
            {
                return;
            }

            _blinkToggle = !_blinkToggle;
            if (icon_transparent != null)
            {
                _notifyIcon.Icon = _blinkToggle ? previousIcon : icon_transparent;
            }
            else
            {
                _notifyIcon.Icon = _blinkToggle ? icon_offline : previousIcon;
            }
        }

        #endregion

        #region MenuItems and MenuGroups

        private MenuItem CreateMenuItem(string text, EventHandler click)
        {
            return new MenuItem(I18N.GetString(text), click);
        }

        private MenuItem CreateMenuGroup(string text, MenuItem[] items)
        {
            return new MenuItem(I18N.GetString(text), items);
        }

        private void LoadMenu()
        {
            this.contextMenu1 = new ContextMenu(new MenuItem[] {
                CreateMenuGroup("System Proxy", new MenuItem[] {
                    this.disableItem = CreateMenuItem("Disable", new EventHandler(this.EnableItem_Click)),
                    this.PACModeItem = CreateMenuItem("PAC", new EventHandler(this.PACModeItem_Click)),
                    this.globalModeItem = CreateMenuItem("Global", new EventHandler(this.GlobalModeItem_Click))
                }),
                this.ServersItem = CreateMenuGroup("Servers", new MenuItem[] {
                    this.SeperatorItem = new MenuItem("-"),
                    this.ConfigItem = CreateMenuItem("Edit Servers...", new EventHandler(this.Config_Click)),
                    new MenuItem("-"),
                    CreateMenuItem("Share Server Config...", new EventHandler(this.QRCodeItem_Click)),
                    CreateMenuItem("Scan QRCode from Screen...", new EventHandler(this.ScanQRCodeItem_Click)),
                    CreateMenuItem("Import URL from Clipboard...", new EventHandler(this.ImportURLItem_Click))
                }),
                CreateMenuGroup("PAC ", new MenuItem[] {
                    this.localPACItem = CreateMenuItem("Local PAC", new EventHandler(this.LocalPACItem_Click)),
                    this.onlinePACItem = CreateMenuItem("Online PAC", new EventHandler(this.OnlinePACItem_Click)),
                    new MenuItem("-"),
                    this.editLocalPACItem = CreateMenuItem("Edit Local PAC File...", new EventHandler(this.EditPACFileItem_Click)),
                    this.updateFromGeositeItem = CreateMenuItem("Update Local PAC from Geosite", new EventHandler(this.UpdatePACFromGeositeItem_Click)),
                    this.editGFWUserRuleItem = CreateMenuItem("Edit User Rule for Geosite...", new EventHandler(this.EditUserRuleFileForGeositeItem_Click)),
                    this.secureLocalPacUrlToggleItem = CreateMenuItem("Secure Local PAC", new EventHandler(this.SecureLocalPacUrlToggleItem_Click)),
                    this.regenerateLocalPacOnUpdateItem = CreateMenuItem("Regenerate local PAC on version update", new EventHandler(this.RegenerateLocalPacOnUpdateItem_Click)),
                    CreateMenuItem("Copy Local PAC URL", new EventHandler(this.CopyLocalPacUrlItem_Click)),
                    this.editOnlinePACItem = CreateMenuItem("Edit Online PAC URL...", new EventHandler(this.UpdateOnlinePACURLItem_Click)),
                }),
                this.proxyItem = CreateMenuItem("Forward Proxy...", new EventHandler(this.proxyItem_Click)),
                this.onlineConfigItem = CreateMenuItem("Online Config...", new EventHandler(this.OnlineConfig_Click)),
                new MenuItem("-"),
                this.AutoStartupItem = CreateMenuItem("Start on Boot", new EventHandler(this.AutoStartupItem_Click)),
                this.ProtocolHandlerItem = CreateMenuItem("Associate ss:// Links", new EventHandler(this.ProtocolHandlerItem_Click)),
                this.ShareOverLANItem = CreateMenuItem("Allow other Devices to connect", new EventHandler(this.ShareOverLANItem_Click)),
                new MenuItem("-"),
                this.hotKeyItem = CreateMenuItem("Edit Hotkeys...", new EventHandler(this.hotKeyItem_Click)),
                CreateMenuGroup("Help", new MenuItem[] {
                    CreateMenuItem("Show Logs...", new EventHandler(this.ShowLogItem_Click)),
                    this.VerboseLoggingToggleItem = CreateMenuItem( "Verbose Logging", new EventHandler(this.VerboseLoggingToggleItem_Click) ),
                    this.ShowPluginOutputToggleItem = CreateMenuItem("Show Plugin Output", new EventHandler(this.ShowPluginOutputToggleItem_Click)),
                    this.WriteI18NFileItem = CreateMenuItem("Write translation template",new EventHandler(WriteI18NFileItem_Click)),
                    CreateMenuGroup("Updates...", new MenuItem[] {
                        CreateMenuItem("Check for Updates...", new EventHandler(this.checkUpdatesItem_Click)),
                        new MenuItem("-"),
                        this.autoCheckUpdatesToggleItem = CreateMenuItem("Check for Updates at Startup", new EventHandler(this.autoCheckUpdatesToggleItem_Click)),
                        this.checkPreReleaseToggleItem = CreateMenuItem("Check Pre-release Version", new EventHandler(this.checkPreReleaseToggleItem_Click)),
                    }),
                    CreateMenuItem("About...", new EventHandler(this.AboutItem_Click)),
                }),
                new MenuItem("-"),
                CreateMenuItem("Quit", new EventHandler(this.Quit_Click))
            });
        }

        #endregion

        private void controller_TrafficChanged(object sender, EventArgs e)
        {
            if (icon == null)
                return;

            if (_isOfflineBlinking)
                return;

            Icon newIcon;

            bool hasInbound = controller.trafficPerSecondQueue.Last().inboundIncreasement > 0;
            bool hasOutbound = controller.trafficPerSecondQueue.Last().outboundIncreasement > 0;

            if (hasInbound && hasOutbound)
                newIcon = icon_both;
            else if (hasInbound)
                newIcon = icon_in;
            else if (hasOutbound)
                newIcon = icon_out;
            else
                newIcon = icon;

            if (newIcon != this.previousIcon)
            {
                this.previousIcon = newIcon;
                _notifyIcon.Icon = newIcon;
            }
        }

        void controller_Errored(object sender, ErrorEventArgs e)
        {
            MessageBox.Show(e.GetException().ToString(), I18N.GetString("Shadowsocks Error: {0}", e.GetException().Message));
        }

        private void controller_ConfigChanged(object sender, EventArgs e)
        {
            LoadCurrentConfiguration();
            UpdateTrayIconAndNotifyText();
        }

        private void LoadCurrentConfiguration()
        {
            Configuration config = controller.GetCurrentConfiguration();
            UpdateServersMenu();
            UpdateSystemProxyItemsEnabledStatus(config);
            ShareOverLANItem.Checked = config.shareOverLan;
            VerboseLoggingToggleItem.Checked = config.isVerboseLogging;
            ShowPluginOutputToggleItem.Checked = config.showPluginOutput;
            AutoStartupItem.Checked = AutoStartup.Check();
            ProtocolHandlerItem.Checked = ProtocolHandler.Check();
            onlinePACItem.Checked = onlinePACItem.Enabled && config.useOnlinePac;
            localPACItem.Checked = !onlinePACItem.Checked;
            secureLocalPacUrlToggleItem.Checked = config.secureLocalPac;
            regenerateLocalPacOnUpdateItem.Checked = config.regeneratePacOnUpdate;
            UpdatePACItemsEnabledStatus();
            UpdateUpdateMenu();
        }

        #region Forms

        private void ShowConfigForm()
        {
            if (configForm != null)
            {
                configForm.Activate();
            }
            else
            {
                configForm = new ConfigForm(controller);
                configForm.Show();
                configForm.Activate();
                configForm.FormClosed += configForm_FormClosed;
            }
        }

        private void ShowLogForm()
        {
            if (logForm != null)
            {
                logForm.Activate();
            }
            else
            {
                logForm = new LogForm(controller);
                logForm.Show();
                logForm.Activate();
                logForm.FormClosed += logForm_FormClosed;
            }
        }

        void logForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            logForm.Dispose();
            logForm = null;
        }

        void configForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            configForm.Dispose();
            configForm = null;
            var config = controller.GetCurrentConfiguration();
            if (config.firstRun)
            {
                CheckUpdateForFirstRun();
                ShowBalloonTip(
                    I18N.GetString("Shadowsocks is here"),
                    I18N.GetString("You can turn on/off Shadowsocks in the context menu"),
                    ToolTipIcon.Info,
                    0
                );
                config.firstRun = false;
            }
        }

        /// <summary>
        /// 释放托盘控制器相关资源。
        /// </summary>
        public void Dispose()
        {
            if (controller != null)
            {
                controller.EnableStatusChanged -= controller_EnableStatusChanged;
                controller.ConfigChanged -= controller_ConfigChanged;
                controller.PACFileReadyToOpen -= controller_FileReadyToOpen;
                controller.UserRuleFileReadyToOpen -= controller_FileReadyToOpen;
                controller.ShareOverLANStatusChanged -= controller_ShareOverLANStatusChanged;
                controller.VerboseLoggingStatusChanged -= controller_VerboseLoggingStatusChanged;
                controller.ShowPluginOutputChanged -= controller_ShowPluginOutputChanged;
                controller.EnableGlobalChanged -= controller_EnableGlobalChanged;
                controller.Errored -= controller_Errored;
                controller.UpdatePACFromGeositeCompleted -= controller_UpdatePACFromGeositeCompleted;
                controller.UpdatePACFromGeositeError -= controller_UpdatePACFromGeositeError;
                controller.TrafficChanged -= controller_TrafficChanged;
            }

            if (updateChecker != null)
            {
                updateChecker.CheckUpdateCompleted -= updateChecker_CheckUpdateCompleted;
                updateChecker = null;
            }

            StopOfflineBlink();

            if (_blinkTimer != null)
            {
                _blinkTimer.Stop();
                _blinkTimer.Tick -= BlinkTimer_Tick;
                _blinkTimer.Dispose();
                _blinkTimer = null;
            }

            if (_connectivityMonitor != null)
            {
                _connectivityMonitor.ConnectivityChanged -= ConnectivityMonitor_ConnectivityChanged;
                _connectivityMonitor.Dispose();
                _connectivityMonitor = null;
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }

        #endregion

        #region Misc

        void ShowBalloonTip(string title, string content, ToolTipIcon icon, int timeout)
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = content;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(timeout);
        }

        void notifyIcon1_BalloonTipClicked(object sender, EventArgs e)
        {
        }

        private void _notifyIcon_BalloonTipClosed(object sender, EventArgs e)
        {
        }

        private void notifyIcon1_Click(object sender, MouseEventArgs e)
        {
            UpdateTrayIconAndNotifyText();
            if (e.Button == MouseButtons.Middle)
            {
                ShowLogForm();
            }
        }

        private void notifyIcon1_DoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowConfigForm();
            }
        }

        private void CheckUpdateForFirstRun()
        {
            Configuration config = controller.GetCurrentConfiguration();
            if (config.firstRun)
                return;
            _isStartupCheck = true;
            Dispatcher.CurrentDispatcher.Invoke(() => updateChecker.CheckForVersionUpdate(3000));
        }

        public void ShowLogForm_HotKey()
        {
            ShowLogForm();
        }

        #endregion

        #region Main menu

        void controller_ShareOverLANStatusChanged(object sender, EventArgs e)
        {
            ShareOverLANItem.Checked = controller.GetCurrentConfiguration().shareOverLan;
        }

        private void proxyItem_Click(object sender, EventArgs e)
        {
            if (forwardProxyWindow == null)
            {
                forwardProxyWindow = new System.Windows.Window()
                {
                    Title = LocalizationProvider.GetLocalizedValue<string>("ForwardProxy"),
                    Height = 400,
                    Width = 280,
                    MinHeight = 400,
                    MinWidth = 280,
                    Content = new ForwardProxyView()
                };
                forwardProxyWindow.Closed += ForwardProxyWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(forwardProxyWindow);
                forwardProxyWindow.Show();
            }
            forwardProxyWindow.Activate();
        }

        private void ForwardProxyWindow_Closed(object sender, EventArgs e)
        {
            forwardProxyWindow = null;
        }

        public void CloseForwardProxyWindow() => forwardProxyWindow.Close();

        private void OnlineConfig_Click(object sender, EventArgs e)
        {
            if (onlineConfigWindow == null)
            {
                onlineConfigWindow = new System.Windows.Window()
                {
                    Title = LocalizationProvider.GetLocalizedValue<string>("OnlineConfigDelivery"),
                    Height = 510,
                    Width = 480,
                    MinHeight = 510,
                    MinWidth = 480,
                    Content = new OnlineConfigView()
                };
                onlineConfigWindow.Closed += OnlineConfigWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(onlineConfigWindow);
                onlineConfigWindow.Show();
            }
            onlineConfigWindow.Activate();
        }

        private void OnlineConfigWindow_Closed(object sender, EventArgs e)
        {
            onlineConfigWindow = null;
        }

        private void hotKeyItem_Click(object sender, EventArgs e)
        {
            if (hotkeysWindow == null)
            {
                hotkeysWindow = new System.Windows.Window()
                {
                    Title = LocalizationProvider.GetLocalizedValue<string>("Hotkeys"),
                    Height = 260,
                    Width = 320,
                    MinHeight = 260,
                    MinWidth = 320,
                    Content = new HotkeysView()
                };
                hotkeysWindow.Closed += HotkeysWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(hotkeysWindow);
                hotkeysWindow.Show();
            }
            hotkeysWindow.Activate();
        }

        private void HotkeysWindow_Closed(object sender, EventArgs e)
        {
            hotkeysWindow = null;
        }

        public void CloseHotkeysWindow() => hotkeysWindow.Close();

        private void ShareOverLANItem_Click(object sender, EventArgs e)
        {
            ShareOverLANItem.Checked = !ShareOverLANItem.Checked;
            controller.ToggleShareOverLAN(ShareOverLANItem.Checked);
        }

        private void AutoStartupItem_Click(object sender, EventArgs e)
        {
            AutoStartupItem.Checked = !AutoStartupItem.Checked;
            if (!AutoStartup.Set(AutoStartupItem.Checked))
            {
                MessageBox.Show(I18N.GetString("Failed to update registry"));
            }
            LoadCurrentConfiguration();
        }

        private void ProtocolHandlerItem_Click(object sender, EventArgs e)
        {
            ProtocolHandlerItem.Checked = !ProtocolHandlerItem.Checked;
            if (!ProtocolHandler.Set(ProtocolHandlerItem.Checked))
            {
                MessageBox.Show(I18N.GetString("Failed to update registry"));
            }
            LoadCurrentConfiguration();
        }

        private void Quit_Click(object sender, EventArgs e)
        {
            controller.Stop();
            _notifyIcon.Visible = false;
            Application.Exit();
        }

        #endregion

        #region System proxy

        private void controller_EnableStatusChanged(object sender, EventArgs e)
        {
            disableItem.Checked = !controller.GetCurrentConfiguration().enabled;
        }

        private void EnableItem_Click(object sender, EventArgs e)
        {
            controller.ToggleEnable(false);
            Configuration config = controller.GetCurrentConfiguration();
            UpdateSystemProxyItemsEnabledStatus(config);
        }

        void controller_EnableGlobalChanged(object sender, EventArgs e)
        {
            globalModeItem.Checked = controller.GetCurrentConfiguration().global;
            PACModeItem.Checked = !globalModeItem.Checked;
        }

        private void UpdateSystemProxyItemsEnabledStatus(Configuration config)
        {
            disableItem.Checked = !config.enabled;
            if (!config.enabled)
            {
                globalModeItem.Checked = false;
                PACModeItem.Checked = false;
            }
            else
            {
                globalModeItem.Checked = config.global;
                PACModeItem.Checked = !config.global;
            }
        }

        private void GlobalModeItem_Click(object sender, EventArgs e)
        {
            controller.ToggleEnable(true);
            controller.ToggleGlobal(true);
            Configuration config = controller.GetCurrentConfiguration();
            UpdateSystemProxyItemsEnabledStatus(config);
        }

        private void PACModeItem_Click(object sender, EventArgs e)
        {
            controller.ToggleEnable(true);
            controller.ToggleGlobal(false);
            Configuration config = controller.GetCurrentConfiguration();
            UpdateSystemProxyItemsEnabledStatus(config);
        }

        #endregion

        #region Server

        private void UpdateServersMenu()
        {
            var items = ServersItem.MenuItems;
            while (items[0] != SeperatorItem)
            {
                items.RemoveAt(0);
            }
            int strategyCount = 0;
            foreach (var strategy in controller.GetStrategies())
            {
                MenuItem item = new MenuItem(strategy.Name);
                item.Tag = strategy.ID;
                item.Click += AStrategyItem_Click;
                items.Add(strategyCount, item);
                strategyCount++;
            }

            // user wants a seperator item between strategy and servers menugroup
            items.Add(strategyCount++, new MenuItem("-"));

            int maxCount = 20;
            int serverCount = 0;
            bool overflow = false;
            bool needAdd = true;
            
            Configuration configuration = controller.GetCurrentConfiguration();
            for (int i = 0; i < configuration.configs.Count; i++)
            {
                try
                {
                    if (overflow)
                    {
                        needAdd = configuration.index >= i;
                        if (needAdd)
                        {
                            i = configuration.index;
                        }
                    }

                    if (needAdd)
                    {
                        var server = configuration.configs[i];
                        Configuration.CheckServer(server);
                        var item = new MenuItem(server.ToString());
                        item.Tag = i;
                        item.Click += AServerItem_Click;
                        items.Add(strategyCount + serverCount, item);
                        serverCount++;
                    }
                    
                    if (overflow)
                    {
                        items.Add(strategyCount + serverCount, new MenuItem($"... more than {maxCount} (total {configuration.configs.Count})", Config_Click));
                        break;
                    }

                    overflow = serverCount > maxCount;
                }
                catch
                {
                }
            }

            foreach (MenuItem item in items)
            {
                if (item.Tag != null && (item.Tag.ToString() == configuration.index.ToString() || item.Tag.ToString() == configuration.strategy))
                {
                    item.Checked = true;
                }
            }
        }

        private void AServerItem_Click(object sender, EventArgs e)
        {
            MenuItem item = (MenuItem)sender;
            controller.SelectServerIndex((int)item.Tag);
        }

        private void AStrategyItem_Click(object sender, EventArgs e)
        {
            MenuItem item = (MenuItem)sender;
            controller.SelectStrategy((string)item.Tag);
        }

        private void Config_Click(object sender, EventArgs e)
        {
            ShowConfigForm();
        }

        void openURLFromQRCode()
        {
            Process.Start(_urlToOpen);
        }

        private void QRCodeItem_Click(object sender, EventArgs e)
        {
            if (serverSharingWindow == null)
            {
                serverSharingWindow = new System.Windows.Window()
                {
                    Title = LocalizationProvider.GetLocalizedValue<string>("ServerSharing"),
                    Height = 400,
                    Width = 660,
                    MinHeight = 400,
                    MinWidth = 660,
                    Content = new ServerSharingView()
                };
                serverSharingWindow.Closed += ServerSharingWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(serverSharingWindow);
                serverSharingWindow.Show();
            }
            serverSharingWindow.Activate();
        }

        private void ServerSharingWindow_Closed(object sender, EventArgs e)
        {
            serverSharingWindow = null;
        }

        private void ScanQRCodeItem_Click(object sender, EventArgs e)
        {
            var result = Utils.ScanQRCodeFromScreen();
            if (result != null)
            {
                if (result.ToLowerInvariant().StartsWith("http://") || result.ToLowerInvariant().StartsWith("https://"))
                {
                    _urlToOpen = result;
                    openURLFromQRCode();
                }
                else if (controller.AddServerBySSURL(result))
                {
                    ShowConfigForm();
                }
                else
                {
                    MessageBox.Show(I18N.GetString("Invalid QR Code content: {0}", result));
                }
                return;
            }
            else
                MessageBox.Show(I18N.GetString("No QRCode found. Try to zoom in or move it to the center of the screen."));
        }

        private void ImportURLItem_Click(object sender, EventArgs e)
        {
            if (controller.AskAddServerBySSURL(Clipboard.GetText(TextDataFormat.Text)))
            {
                ShowConfigForm();
            }
        }

        #endregion

        #region PAC

        private void LocalPACItem_Click(object sender, EventArgs e)
        {
            if (!localPACItem.Checked)
            {
                localPACItem.Checked = true;
                onlinePACItem.Checked = false;
                controller.UseOnlinePAC(false);
                UpdatePACItemsEnabledStatus();
            }
        }

        private void OnlinePACItem_Click(object sender, EventArgs e)
        {
            if (!onlinePACItem.Checked)
            {
                if (string.IsNullOrEmpty(controller.GetCurrentConfiguration().pacUrl))
                {
                    UpdateOnlinePACURLItem_Click(sender, e);
                }
                if (!string.IsNullOrEmpty(controller.GetCurrentConfiguration().pacUrl))
                {
                    localPACItem.Checked = false;
                    onlinePACItem.Checked = true;
                    controller.UseOnlinePAC(true);
                }
                UpdatePACItemsEnabledStatus();
            }
        }

        private void UpdateOnlinePACURLItem_Click(object sender, EventArgs e)
        {
            string origPacUrl = controller.GetCurrentConfiguration().pacUrl;
            string pacUrl = Microsoft.VisualBasic.Interaction.InputBox(
                I18N.GetString("Please input PAC Url"),
                I18N.GetString("Edit Online PAC URL"),
                origPacUrl, -1, -1);
            if (!string.IsNullOrEmpty(pacUrl) && pacUrl != origPacUrl)
            {
                controller.SavePACUrl(pacUrl);
            }
        }

        private void SecureLocalPacUrlToggleItem_Click(object sender, EventArgs e)
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            controller.ToggleSecureLocalPac(!configuration.secureLocalPac);
        }

        private void RegenerateLocalPacOnUpdateItem_Click(object sender, EventArgs e)
        {
            var config = controller.GetCurrentConfiguration();
            controller.ToggleRegeneratePacOnUpdate(!config.regeneratePacOnUpdate);
        }

        private void CopyLocalPacUrlItem_Click(object sender, EventArgs e)
        {
            controller.CopyPacUrl();
        }

        private void UpdatePACItemsEnabledStatus()
        {
            if (this.localPACItem.Checked)
            {
                this.editLocalPACItem.Enabled = true;
                this.updateFromGeositeItem.Enabled = true;
                this.editGFWUserRuleItem.Enabled = true;
                this.editOnlinePACItem.Enabled = false;
            }
            else
            {
                this.editLocalPACItem.Enabled = false;
                this.updateFromGeositeItem.Enabled = false;
                this.editGFWUserRuleItem.Enabled = false;
                this.editOnlinePACItem.Enabled = true;
            }
        }

        private void EditPACFileItem_Click(object sender, EventArgs e)
        {
            controller.TouchPACFile();
        }

        private async void UpdatePACFromGeositeItem_Click(object sender, EventArgs e)
        {
            await GeositeUpdater.UpdatePACFromGeosite();
        }

        private void EditUserRuleFileForGeositeItem_Click(object sender, EventArgs e)
        {
            controller.TouchUserRuleFile();
        }

        void controller_FileReadyToOpen(object sender, ShadowsocksController.PathEventArgs e)
        {
            string argument = @"/select, " + e.Path;

            Process.Start("explorer.exe", argument);
        }

        void controller_UpdatePACFromGeositeError(object sender, System.IO.ErrorEventArgs e)
        {
            ShowBalloonTip(I18N.GetString("Failed to update PAC file"), e.GetException().Message, ToolTipIcon.Error, 5000);
            logger.LogUsefulException(e.GetException());
        }

        void controller_UpdatePACFromGeositeCompleted(object sender, GeositeResultEventArgs e)
        {
            string result = e.Success
                ? I18N.GetString("PAC updated")
                : I18N.GetString("No updates found. Please report to Geosite if you have problems with it.");
            ShowBalloonTip(I18N.GetString("Shadowsocks"), result, ToolTipIcon.Info, 1000);
        }

        #endregion

        #region Help

        void controller_VerboseLoggingStatusChanged(object sender, EventArgs e)
        {
            VerboseLoggingToggleItem.Checked = controller.GetCurrentConfiguration().isVerboseLogging;
        }

        void controller_ShowPluginOutputChanged(object sender, EventArgs e)
        {
            ShowPluginOutputToggleItem.Checked = controller.GetCurrentConfiguration().showPluginOutput;
        }

        private void VerboseLoggingToggleItem_Click(object sender, EventArgs e)
        {
            VerboseLoggingToggleItem.Checked = !VerboseLoggingToggleItem.Checked;
            controller.ToggleVerboseLogging(VerboseLoggingToggleItem.Checked);
        }

        private void ShowLogItem_Click(object sender, EventArgs e)
        {
            ShowLogForm();
        }

        private void ShowPluginOutputToggleItem_Click(object sender, EventArgs e)
        {
            ShowPluginOutputToggleItem.Checked = !ShowPluginOutputToggleItem.Checked;
            controller.ToggleShowPluginOutput(ShowPluginOutputToggleItem.Checked);
        }

        private void WriteI18NFileItem_Click(object sender, EventArgs e)
        {
            File.WriteAllText(I18N.I18N_FILE, Resources.i18n_csv, Encoding.UTF8);
        }

        #endregion

        #region Update

        void updateChecker_CheckUpdateCompleted(object sender, EventArgs e)
        {
            if (!_isStartupCheck && updateChecker.NewReleaseZipFilename == null)
            {
                ShowBalloonTip(I18N.GetString("Shadowsocks"), I18N.GetString("No update is available"), ToolTipIcon.Info, 5000);
            }
            _isStartupCheck = false;
        }

        private void UpdateUpdateMenu()
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            autoCheckUpdatesToggleItem.Checked = configuration.autoCheckUpdate;
            checkPreReleaseToggleItem.Checked = configuration.checkPreRelease;
        }

        private void autoCheckUpdatesToggleItem_Click(object sender, EventArgs e)
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            controller.ToggleCheckingUpdate(!configuration.autoCheckUpdate);
            UpdateUpdateMenu();
        }

        private void checkPreReleaseToggleItem_Click(object sender, EventArgs e)
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            controller.ToggleCheckingPreRelease(!configuration.checkPreRelease);
            UpdateUpdateMenu();
        }

        private async void checkUpdatesItem_Click(object sender, EventArgs e)
        {
            await updateChecker.CheckForVersionUpdate();
        }

        private void AboutItem_Click(object sender, EventArgs e)
        {
            Process.Start("https://github.com/shadowsocks/shadowsocks-windows");
        }

        #endregion
    }
}
