using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Collections.Concurrent;
using System.IO;

namespace PortForwarding
{
    class Program
    {
        public static int FromPort { get; set; }
        public static string ToAddress { get; set; }
        public static int ToPort { get; set; }

        static List<Forwarding> forwardingList = new List<Forwarding>();
        static List<TcpClient> incomingList = new List<TcpClient>();

        static void Main(string[] args)
        {
            FromPort = 5432;
            ToAddress = "192.168.241.66";
            ToPort = 15432;

            try
            {
                if (args.Length > 0)
                {
                    FromPort = int.Parse(args[0]);
                    ToAddress = args[1];
                    ToPort = int.Parse(args[2]);
                }
            }
            catch (Exception)
            {
                Console.WriteLine(@"
Usage:
PortForwarding from_port to_address to_port");
            }

            Console.WriteLine("FromPort: {0}, ToAddress: {1}, ToPort: {2}", FromPort, ToAddress, ToPort);

            TcpListener tcpServer = new TcpListener(IPAddress.Any, FromPort);
            tcpServer.Start();

            new Thread(Dispatcher).Start();

            while (true)
            {
                TcpClient client = tcpServer.AcceptTcpClient();
                Console.WriteLine("Client connected: {0}", client.Client.RemoteEndPoint);

                lock (incomingList)
                {
                    incomingList.Add(client);
                }
            }
        }

        static void Dispatcher()
        {
            TimeSpan expiredTime = TimeSpan.FromMinutes(1);
            while (true)
            {
                // Add new clients.
                lock (incomingList)
                {
                    foreach (var item in incomingList)
                    {
                        var forwarding = new Forwarding()
                        {
                            Client = item,
                            LastActiveTime = DateTime.Now
                        };

                        forwardingList.Add(forwarding);
                    }

                    incomingList.Clear();
                }

                // Wait all messages are forwarded.
                var removingList = new List<Forwarding>();
                foreach (var forwarding in forwardingList)
                {
                    if (forwarding.Client.Available > 0
                        || forwarding.Server == null
                        || forwarding.Server.Available > 0)
                    {
                        if (!Invoke(() =>
                        {
                            Forward(forwarding);
                        }))
                        {
                            removingList.Add(forwarding);
                        }
                    }
                    else if (DateTime.Now - forwarding.LastActiveTime > expiredTime)
                    {
                        if (!Invoke(() =>
                        {
                            if (SocketConnected(forwarding.Client) && SocketConnected(forwarding.Server))
                            {
                                forwarding.LastActiveTime = DateTime.Now;
                                return;
                            }

                            removingList.Add(forwarding);
                        }))
                        {
                            removingList.Add(forwarding);
                        }
                    }
                }

                // Remove expired session.
                foreach (var item in removingList)
                {
                    Invoke(() =>
                    {
                        Console.WriteLine("Client closed: {0}", item.Client.Client.RemoteEndPoint);
                        item.Client.Close();
                        item.Server.Close();
                    });
                    forwardingList.Remove(item);
                }

                if (forwardingList.Count <= 0)
                {
                    Console.WriteLine("Idle.");
                    Thread.Sleep(1000);
                }
            }
        }

        static void Forward(object obj)
        {
            var forwarding = (Forwarding)obj;

            if (forwarding.Server == null)
            {
                TcpClient server = new TcpClient();
                server.Connect(new IPEndPoint(IPAddress.Parse(ToAddress), ToPort));
                forwarding.Server = server;
            }

            var forward1 = Forward(forwarding.Client, forwarding.Server);
            var forward2 = Forward(forwarding.Server, forwarding.Client);
            if (forward1 > 0 || forward2 > 0)
            {
                forwarding.LastActiveTime = DateTime.Now;

                if (forward1 > 0)
                {
                    Console.WriteLine("{0}  > {1}: {2}.", forwarding.Client.Client.RemoteEndPoint, forwarding.Server.Client.RemoteEndPoint, forward1);
                }

                if (forward2 > 0)
                {
                    Console.WriteLine("{0} <  {1}: {2}.", forwarding.Client.Client.RemoteEndPoint, forwarding.Server.Client.RemoteEndPoint, forward2);
                }
            }
        }

        static int Forward(TcpClient from, TcpClient to)
        {
            if (!from.Connected || !to.Connected || from.Available <= 0)
            {
                return 0;
            }

            byte[] buffer = new byte[102400];
            int received = from.Client.Receive(buffer, buffer.Length, SocketFlags.None);
            to.Client.Send(buffer, received, SocketFlags.None);

            return received;
        }

        static bool Invoke(Action action)
        {
            try
            {
                action.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                var msg = string.Format("[0] {1}\r\n", DateTime.Now, ex);
                Console.Write(msg);
                File.AppendAllText("error_log.txt", msg);
            }

            return false;
        }

        static bool SocketConnected(TcpClient client)
        {
            bool part1 = client.Client.Poll(1000, SelectMode.SelectRead);
            bool part2 = (client.Client.Available == 0);
            if (part1 & part2)
            {
                return false;
            }

            return true;
        }
    }

    public class Forwarding
    {
        public TcpClient Client { get; set; }

        public TcpClient Server { get; set; }
        
        public DateTime LastActiveTime { get; set; }
    }
}
