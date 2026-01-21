using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace DadBoard.Gate;

sealed class MeterForm : Form, IProgress<double>
{
    private readonly ProgressBar _bar = new();
    private readonly Label _label = new();
    private readonly CancellationTokenSource _cts = new();

    public MeterForm(string title, string prompt)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(520, 180);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _label.Text = prompt;
        _label.Dock = DockStyle.Top;
        _label.Height = 60;
        _label.TextAlign = ContentAlignment.MiddleCenter;
        _label.Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold);

        _bar.Dock = DockStyle.Top;
        _bar.Height = 28;
        _bar.Maximum = 100;

        var cancel = new Button
        {
            Text = "Cancel",
            Dock = DockStyle.Bottom,
            Height = 32
        };
        cancel.Click += (_, _) => _cts.Cancel();

        Controls.Add(cancel);
        Controls.Add(_bar);
        Controls.Add(_label);
    }

    public CancellationToken CancellationToken => _cts.Token;

    public void Report(double value)
    {
        var pct = (int)Math.Clamp(value * 100, 0, 100);
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => _bar.Value = pct));
        }
        else
        {
            _bar.Value = pct;
        }
    }
}
