using System;
using System.Drawing;
using System.Windows.Forms;

namespace DadBoard.App;

enum FirstRunChoice
{
    Install,
    Portable
}

sealed class FirstRunForm : Form
{
    private FirstRunChoice _choice = FirstRunChoice.Portable;

    private FirstRunForm()
    {
        Text = "DadBoard Setup";
        Size = new Size(420, 200);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var label = new Label
        {
            Text = "DadBoard is not installed yet.\n\nInstall is recommended for auto-start and shared configs.",
            Dock = DockStyle.Top,
            Height = 80
        };

        var installButton = new Button
        {
            Text = "Install (recommended)",
            DialogResult = DialogResult.OK,
            Width = 160,
            Height = 32
        };

        var portableButton = new Button
        {
            Text = "Run without installing",
            DialogResult = DialogResult.Cancel,
            Width = 160,
            Height = 32
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10),
            AutoSize = true
        };

        buttonPanel.Controls.Add(installButton);
        buttonPanel.Controls.Add(portableButton);

        Controls.Add(label);
        Controls.Add(buttonPanel);

        AcceptButton = installButton;
        CancelButton = portableButton;

        installButton.Click += (_, _) => { _choice = FirstRunChoice.Install; Close(); };
        portableButton.Click += (_, _) => { _choice = FirstRunChoice.Portable; Close(); };
    }

    public static FirstRunChoice ShowChoice()
    {
        using var form = new FirstRunForm();
        form.ShowDialog();
        return form._choice;
    }
}
