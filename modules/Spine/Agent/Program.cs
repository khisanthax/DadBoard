using System;
using System.Threading;

namespace DadBoard.Agent;

static class Program
{
    static void Main()
    {
        var service = new AgentService();
        service.Start();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => service.Stop();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            service.Stop();
        };

        Thread.Sleep(Timeout.Infinite);
    }
}
