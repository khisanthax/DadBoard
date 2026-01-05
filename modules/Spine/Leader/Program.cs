using System;
using System.Windows.Forms;

namespace DadBoard.Leader;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var service = new LeaderService();
        var form = new LeaderForm(service);
        Application.Run(form);
    }
}
