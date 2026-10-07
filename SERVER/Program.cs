using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;

namespace ConsoleGameServer
{
    internal class Program
    {
        static void Main(string[] args)
        {
            new GameServer(7777).Run();
        }
    }

    // =========================================================
    // GAME SERVER
    // The server machine is Player 1 (left paddle, W/S or arrows).
    // The connecting client is Player 2 (right paddle).
    // =========================================================
    public class GameServer
    {
        private readonly int port;
        private TcpListener listener;

        private TcpClient client;
        private NetworkStream stream;
        private readonly object writeLock = new object();

        private volatile bool connected;
        private volatile bool quit;

        private readonly object stateLock = new object();
        private GameState gameState = new GameState();

        private readonly Random random = new Random();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;

        public GameServer(int port)
        {
            this.port = port;
        }

        // =========================================================
        // RUN: wait for a client -> play -> back to waiting
        // =========================================================
        public void Run()
        {
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                Console.SetWindowSize(121, 35);
                Console.SetBufferSize(121, 35);
            }
            catch { }

            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            while (!quit)
            {
                if (!WaitForClient())
                    break;

                PlaySession();
                CleanupClient();
            }

            listener.Stop();
            Console.CursorVisible = true;
            Console.Clear();
            Console.WriteLine("Server stopped.");
        }

        // =========================================================
        // WAITING SCREEN
        // =========================================================
        private bool WaitForClient()
        {
            Console.CursorVisible = true;
            Console.Clear();
            Console.WriteLine("=== PONG SERVER ===");
            Console.WriteLine();
            Console.WriteLine($"Listening on port {port}");
            Console.WriteLine("Server IP address(es) - give one to the client:");

            foreach (string ip in GetLocalIPs())
                Console.WriteLine($"    {ip}");

            Console.WriteLine();
            Console.WriteLine("Waiting for a client to connect...  (ESC to quit)");

            while (!listener.Pending())
            {
                while (Console.KeyAvailable)
                {
                    if (Console.ReadKey(true).Key == ConsoleKey.Escape)
                        return false;
                }

                Thread.Sleep(50);
            }

            client = listener.AcceptTcpClient();
            client.NoDelay = true;
            stream = client.GetStream();
            connected = true;

            return true;
        }

        private static string[] GetLocalIPs()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToArray();
            }
            catch
            {
                return new[] { "(unable to detect - run ipconfig / ip a)" };
            }
        }

        // =========================================================
        // GAME SESSION
        // =========================================================
        private void PlaySession()
        {
            lock (stateLock)
                gameState = new GameState();

            ResetBall();

            Console.Clear();
            prev = null;
            Console.CursorVisible = false;

            Send(new NetMessage { Type = "PLAYER_ID", PlayerId = 1 });

            Thread receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();

            const int delay = 1000 / 30;

            while (connected && !quit)
            {
                DateTime start = DateTime.Now;

                HandleLocalInput();

                string json;

                lock (stateLock)
                {
                    UpdateGame();
                    json = JsonSerializer.Serialize(
                        new NetMessage { Type = "STATE", State = gameState });
                }

                SendRaw(json);
                Render();

                int sleep = delay - (int)(DateTime.Now - start).TotalMilliseconds;

                if (sleep > 0)
                    Thread.Sleep(sleep);
            }

            connected = false;
        }

        private void CleanupClient()
        {
            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }

            stream = null;
            client = null;
        }

        // =========================================================
        // UPDATE GAME (called with stateLock held)
        // =========================================================
        private void UpdateGame()
        {
            GameState g = gameState;

            g.PixelX += g.PixelVelocityX;
            g.PixelY += g.PixelVelocityY;

            if (g.PixelY <= 1)
            {
                g.PixelY = 1;
                g.PixelVelocityY = Math.Abs(g.PixelVelocityY);
            }

            if (g.PixelY >= g.Height - 2)
            {
                g.PixelY = g.Height - 2;
                g.PixelVelocityY = -Math.Abs(g.PixelVelocityY);
            }

            // Left paddle (server player)
            if (g.PixelX <= 3 && g.PixelVelocityX < 0 &&
                g.PixelY >= g.Player1Y && g.PixelY <= g.Player1Y + g.PaddleHeight)
            {
                g.PixelVelocityX = Math.Abs(g.PixelVelocityX);
            }

            // Right paddle (client player)
            if (g.PixelX >= g.Width - 4 && g.PixelVelocityX > 0 &&
                g.PixelY >= g.Player2Y && g.PixelY <= g.Player2Y + g.PaddleHeight)
            {
                g.PixelVelocityX = -Math.Abs(g.PixelVelocityX);
            }

            if (g.PixelX < 0)
            {
                g.Score2++;
                ResetBallLocked();
            }

            if (g.PixelX > g.Width)
            {
                g.Score1++;
                ResetBallLocked();
            }
        }

        private void ResetBall()
        {
            lock (stateLock)
                ResetBallLocked();
        }

        private void ResetBallLocked()
        {
            gameState.PixelX = gameState.Width / 2;
            gameState.PixelY = gameState.Height / 2;
            gameState.PixelVelocityX = random.Next(0, 2) == 0 ? -1 : 1;
            gameState.PixelVelocityY = random.Next(0, 2) == 0 ? -1 : 1;
        }

        // =========================================================
        // INPUT: LOCAL (server player = Player 1)
        // =========================================================
        private void HandleLocalInput()
        {
            // Only drain the key buffer to catch ESC
            while (Console.KeyAvailable)
            {
                if (Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    quit = true;
                    return;
                }
            }

            if (IsDown(VK_UP)) MovePaddle(2, "UP");
            if (IsDown(VK_DOWN)) MovePaddle(2, "DOWN");
        }

        private void MovePaddle(int playerId, string direction)
        {
            lock (stateLock)
            {
                int max = gameState.Height - gameState.PaddleHeight - 1;
                int delta = direction == "UP" ? -1 : direction == "DOWN" ? 1 : 0;

                if (playerId == 1)
                    gameState.Player1Y = Math.Clamp(gameState.Player1Y + delta, 1, max);
                else
                    gameState.Player2Y = Math.Clamp(gameState.Player2Y + delta, 1, max);
            }
        }

        // =========================================================
        // INPUT: REMOTE (client player = Player 2)
        // =========================================================
        private void ReceiveLoop()
        {
            try
            {
                using StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);

                while (connected)
                {
                    string line = reader.ReadLine();

                    if (line == null)
                        break;

                    try
                    {
                        NetMessage msg = JsonSerializer.Deserialize<NetMessage>(line);

                        if (msg != null && msg.Type == "INPUT")
                            MovePaddle(1, msg.Key);
                    }
                    catch (JsonException) { }
                }
            }
            catch { }

            connected = false;
        }

        // =========================================================
        // SEND
        // =========================================================
        private void Send(NetMessage message)
        {
            SendRaw(JsonSerializer.Serialize(message));
        }

        private void SendRaw(string json)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json + "\n");

                lock (writeLock)
                    stream.Write(data, 0, data.Length);
            }
            catch
            {
                connected = false;
            }
        }

        // =========================================================
        // RENDER
        // =========================================================
        private char[,] prev;   // add this field to the class

        private void Render()
        {
            GameState g = gameState;

            char[,] buffer = new char[g.Height, g.Width];

            for (int y = 0; y < g.Height; y++)
                for (int x = 0; x < g.Width; x++)
                    buffer[y, x] = ' ';

            for (int x = 0; x < g.Width; x++)
            {
                buffer[0, x] = '█';
                buffer[g.Height - 1, x] = '█';
            }

            for (int y = 1; y < g.Height - 1; y++)
                buffer[y, g.Width / 2] = '│';

            for (int i = 0; i < g.PaddleHeight; i++)
            {
                int ly = g.Player1Y + i;
                int ry = g.Player2Y + i;

                if (ly > 0 && ly < g.Height - 1) buffer[ly, 2] = '█';
                if (ry > 0 && ry < g.Height - 1) buffer[ry, g.Width - 3] = '█';
            }

            int bx = (int)g.PixelX;
            int by = (int)g.PixelY;

            if (bx > 0 && bx < g.Width - 1 && by > 0 && by < g.Height - 1)
                buffer[by, bx] = '▓';

            string s1 = g.Score1.ToString();
            string s2 = g.Score2.ToString();

            for (int i = 0; i < s1.Length; i++)
                buffer[1, g.Width / 4 + i] = s1[i];

            for (int i = 0; i < s2.Length; i++)
                buffer[1, g.Width - g.Width / 4 + i] = s2[i];

            // First frame (or size change): clear and draw everything once
            bool full = prev == null ||
                        prev.GetLength(0) != g.Height ||
                        prev.GetLength(1) != g.Width;

            if (full)
            {
                Console.Clear();
                prev = new char[g.Height, g.Width];

                for (int y = 0; y < g.Height; y++)
                    for (int x = 0; x < g.Width; x++)
                        prev[y, x] = '\0';   // forces every cell to be drawn

                Console.SetCursorPosition(0, g.Height);
                Console.Write("You are PLAYER 2 (right) - W/S or Up/Down to move, ESC to quit");
            }

            // Draw only the cells that changed
            for (int y = 0; y < g.Height; y++)
            {
                for (int x = 0; x < g.Width; x++)
                {
                    if (buffer[y, x] == prev[y, x])
                        continue;

                    Console.SetCursorPosition(x, y);
                    Console.Write(buffer[y, x]);
                    prev[y, x] = buffer[y, x];
                }
            }
        }
    }

    // =========================================================
    // GAME STATE
    // =========================================================
    public class GameState
    {
        public int Width { get; set; } = 120;
        public int Height { get; set; } = 30;

        public int PaddleHeight { get; set; } = 5;

        public double PixelX { get; set; } = 60;
        public double PixelY { get; set; } = 15;

        public double PixelVelocityX { get; set; } = 1;
        public double PixelVelocityY { get; set; } = 1;

        public int Player1Y { get; set; } = 10;
        public int Player2Y { get; set; } = 10;

        public int Score1 { get; set; }
        public int Score2 { get; set; }
    }

    // =========================================================
    // NETWORK MESSAGE
    // =========================================================
    public class NetMessage
    {
        public string Type { get; set; }
        public int PlayerId { get; set; }
        public string Key { get; set; }
        public GameState State { get; set; }
    }
}