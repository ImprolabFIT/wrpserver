using WRPServer.Cameras;
using WRPServer.Network.Server;

namespace WRPServer
{
    class App
    {
        private readonly ServerContext ctx;
        public App()
        {
            ctx = new ServerContext();
        }

        public void start()
        {
            ServerSocket.StartListening(ctx);
        }
    }
}
