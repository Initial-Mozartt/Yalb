using System;
using System.Linq;
using System.Windows.Forms;

namespace Yalb
{
    public class DebugLogForm : Form
    {
        private TextBox _textBox;
        private System.Windows.Forms.Timer _timer;

        public DebugLogForm()
        {
            Text = "Yalb Debug Log";
            Width = 900;
            Height = 600;

            _textBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill,
                Font = new System.Drawing.Font("Consolas", 10f),
                BackColor = System.Drawing.Color.Black,
                ForeColor = System.Drawing.Color.LightGreen
            };

            var clearBtn = new Button { Text = "Clear", Dock = DockStyle.Bottom, Height = 28 };
            clearBtn.Click += (s, e) => { /* keep in-memory, but clear view */ _textBox.Clear(); };

            Controls.Add(_textBox);
            Controls.Add(clearBtn);

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) => RefreshLogs();
            _timer.Start();

            RefreshLogs();
        }

        public void RefreshLogs()
        {
            try
            {
                var lines = YalbLogger.RecentLines;
                _textBox.SuspendLayout();
                _textBox.Lines = lines.ToArray();
                if (_textBox.TextLength > 0)
                    _textBox.SelectionStart = _textBox.TextLength;
                _textBox.ResumeLayout();
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
