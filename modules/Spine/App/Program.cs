using System;
using System.Windows.Forms;

namespace DadBoard.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var context = new TrayAppContext(args);
        Application.Run(context);
    }
}
