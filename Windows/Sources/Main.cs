using System;
using System.Linq;
using System.Threading;

namespace ClipBubble
{
    static class Program
    {
        /// A second launch asks the running copy to open its panel instead of starting another bubble.
        public const string OpenEvent = @"Local\studio.krnl.clipbubble.open";

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--mcp"))
            {
                Mcp.Run();
                return 0;
            }
            using var single = new Mutex(true, @"Local\studio.krnl.clipbubble", out var first);
            if (!first)
            {
                if (EventWaitHandle.TryOpenExisting(OpenEvent, out var open)) open.Set();
                return 0;
            }
            new App().Run();
            return 0;
        }
    }
}

// Lets records compile on .NET Framework.
namespace System.Runtime.CompilerServices
{
    static class IsExternalInit { }
}
