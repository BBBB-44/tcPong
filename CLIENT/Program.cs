using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ConsoleGameClient
{
    internal class Program
    {
        static void Main(string[] args)
        {
            new GameClient(7777).Run();
        }
    }

    // =========================================================
    // CLIENT (Player 2, right paddle)
    // =========================================================
    public class GameClient
    {
        private readonly int port;

        private TcpClient client;
        private NetworkStream stream;
        private readonly object writeLock = new object();

        private volatile bool connected;
        private volatile bool quit;

        private volatile GameState gameState = new GameState();

        private string lastIp = "127.0.0.1";
        private string lastError = "";

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private const int VK_W = 0x57;
        private const int VK_S = 0x53;

        private readonly Stopwatch inputTimer = Stopwatch.StartNew();

        public GameClient(int port)
        {
            this.port = port;
        }

        // =========================================================
        // RUN: connection screen -> game -> (lost) connection screen
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

            while (!quit)
            {
                if (!ConnectionScreen())
                    continue;

                PlaySession();
                Cleanup();

                if (!quit)
                    lastError = "Connection lost.";
            }

            Console.CursorVisible = true;
            Console.Clear();
        }

        // =========================================================
        // CONNECTION SCREEN
        // =========================================================
        private bool ConnectionScreen()
        {
            Console.CursorVisible = true;
            Console.Clear();
            Console.WriteLine("=== PONG CLIENT ===");
            Console.WriteLine();

            if (lastError != "")
            {
                Console.WriteLine(lastError);
                Console.WriteLine();
            }

            Console.Write($"Server IP [{lastIp}] (type 'q' to quit): ");
            string input = Console.ReadLine();

            if (input == null || input.Trim().ToLower() == "q")
            {
                quit = true;
                return false;
            }

            input = input.Trim();

            if (input != "")
                lastIp = input;

            Console.WriteLine($"Connecting to {lastIp}:{port} ...");

            try
            {
                client = new TcpClient();
                client.NoDelay = true;

                // 5 second connection timeout
                IAsyncResult ar = client.BeginConnect(lastIp, port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    client.Close();
                    lastError = "Connection timed out.";
                    return false;
                }

                client.EndConnect(ar);

                stream = client.GetStream();
                connected = true;
                lastError = "";
                return true;
            }
            catch (Exception ex)
            {
                lastError = $"Could not connect: {ex.Message}";
                Cleanup();
                return false;
            }
        }

        private void Cleanup()
        {
            connected = false;

            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }

            stream = null;
            client = null;
        }

        // =========================================================
        // GAME SESSION
        // =========================================================
        private void PlaySession()
        {
            gameState = new GameState();

            Console.Clear();
            prev = null; // reset previous buffer to force full redraw
            Console.CursorVisible = false;

            Thread receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();

            while (connected && !quit)
            {
                HandleInput();
                Render();
                Thread.Sleep(16);
            }

            connected = false;
        }

        // =========================================================
        // RECEIVE LOOP
        // =========================================================
        private void ReceiveLoop()
        {
            try
            {
                using StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);

                while (connected)
                {
                    string line = reader.ReadLine();

                    if (line == null)
                        break;

                    try
                    {
                        NetMessage msg = JsonSerializer.Deserialize<NetMessage>(line);

                        if (msg?.Type == "STATE" && msg.State != null)
                            gameState = msg.State;
                    }
                    catch (JsonException) { }
                }
            }
            catch { }

            connected = false;
        }

        // =========================================================
        // INPUT
        // =========================================================
        private void HandleInput()
        {
            while (Console.KeyAvailable)
            {
                if (Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    quit = true;
                    return;
                }
            }

            // Send at the server's tick rate (~30/s), otherwise the
            // client paddle would move twice as fast as the server's
            if (inputTimer.ElapsedMilliseconds < 33)
                return;

            inputTimer.Restart();

            if (IsDown(VK_W)) SendInput("UP");
            if (IsDown(VK_S)) SendInput("DOWN");
        }

        private void SendInput(string key)
        {
            try
            {
                string json = JsonSerializer.Serialize(new NetMessage { Type = "INPUT", Key = key });
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

        public double PixelVelocityX { get; set; }
        public double PixelVelocityY { get; set; }

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