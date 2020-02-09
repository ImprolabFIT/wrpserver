using System;
using System.Runtime.InteropServices;
using System.Configuration;

namespace WRPServer
{
    public class Program
    {

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // Ukoncovani procesu
        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);
        private delegate bool EventHandler(CtrlType sig);
        static EventHandler _handler;

        enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        public static int Main(String[] args)
        {
            // Spusti server
            log.Info("Spoustim aplikaci.");

            log.Info("Připravuji zachycení Control+C pro vypnutí");
            // Shutdown hooks 
            // Neošetřená výjimka - handler
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(MyHandler);
            // Ukončení procesu - handler na Control+C
            _handler += new EventHandler(Handler);
            SetConsoleCtrlHandler(_handler, true);

            // Spusteni serveru

            log.Info("Spoustim řídící aplikaci a předávám jí velení");
            App app = new App();
            app.start();

            // Sem bychom se neměli dostat...
            return 0;
        }

        private static void MyHandler(object sender, UnhandledExceptionEventArgs args)
        {
            log.Error("Neosetrena vyjimka detekovana: " + ((Exception)args.ExceptionObject).Message);
            // TODO Unlock all cameras
            //Program.ctx.ReleasePylonResources();
            log.Info("Aplikace bude ukoncena.");
            Environment.Exit(1);
        }

        private static bool Handler(CtrlType sig)
        {
            // Switch je tu zbytečný, spíš pro případ, že bychom chtěli různě reagovat na různé Control signály.
            switch (sig)
            {
                case CtrlType.CTRL_C_EVENT:
                case CtrlType.CTRL_LOGOFF_EVENT:
                case CtrlType.CTRL_SHUTDOWN_EVENT:
                case CtrlType.CTRL_CLOSE_EVENT:
                default:
                    log.Info("Volan shutdown hook.");
                    //Program.ctx.ReleasePylonResources();
                    log.Info("Aplikace bude ukoncena.");
                    Environment.Exit(0);
                    // Aby neřvalo VS...
                    break;
            }
            // Pro dodrzeni kontraktu...
            return false;
        }
    }
}
