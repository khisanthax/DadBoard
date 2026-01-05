using System;
using System.Windows.Forms;

namespace GateAgent;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var context = new GateAgentContext();
        Application.Run(context);
    }
}
