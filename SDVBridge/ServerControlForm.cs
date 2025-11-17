using System;
using System.Drawing;
using System.Windows.Forms;
using SAS.Shared.AddIns;
using SAS.Tasks.Toolkit.Controls;
using SDVBridge.Server;

namespace SDVBridge
{
    internal sealed class ServerControlForm : TaskForm
    {
        private TextBox portTextBox;
        private Button startButton;
        private Button stopButton;
        private Label statusLabel;

        public ISASTaskConsumer Consumer { get; set; }

        public ServerControlForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "SDVBridge REST Server";
            ClientSize = new Size(360, 150);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var portLabel = new Label
            {
                Text = "Port:",
                AutoSize = true,
                Location = new Point(20, 20)
            };

            portTextBox = new TextBox
            {
                Location = new Point(70, 16),
                Width = 120,
                Text = WebServerManager.Port.ToString()
            };

            startButton = new Button
            {
                Text = "Start",
                Location = new Point(20, 60),
                Width = 100
            };
            startButton.Click += (_, __) => StartServer();

            stopButton = new Button
            {
                Text = "Stop",
                Location = new Point(140, 60),
                Width = 100
            };
            stopButton.Click += (_, __) => StopServer();

            statusLabel = new Label
            {
                AutoSize = true,
                Location = new Point(20, 105),
                Width = 320
            };

            Controls.Add(portLabel);
            Controls.Add(portTextBox);
            Controls.Add(startButton);
            Controls.Add(stopButton);
            Controls.Add(statusLabel);

            UpdateButtonState();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UpdateStatus();
        }

        private void StartServer()
        {
            if (!int.TryParse(portTextBox.Text, out var port) || port <= 0 || port > 65535)
            {
                MessageBox.Show(this, "Enter a valid TCP port number (1-65535).", "SDVBridge", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                WebServerManager.Start(Consumer, port);
                UpdateStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not start the server: {ex.Message}", "SDVBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateButtonState();
            }
        }

        private void StopServer()
        {
            WebServerManager.Stop();
            UpdateStatus();
            UpdateButtonState();
        }

        private void UpdateStatus()
        {
            if (WebServerManager.IsRunning)
            {
                statusLabel.Text = $"Server running at http://127.0.0.1:{WebServerManager.Port}/";
            }
            else
            {
                statusLabel.Text = "Server stopped.";
            }
        }

        private void UpdateButtonState()
        {
            bool running = WebServerManager.IsRunning;
            startButton.Enabled = !running;
            stopButton.Enabled = running;
        }
    }
}
