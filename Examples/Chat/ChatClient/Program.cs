﻿using ChatMessages;
using Akka;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var config = ConfigurationFactory.ParseString(@"
akka {  
    log-config-on-start = on
    stdout-loglevel = INFO
    loglevel = ERROR
    actor {
        provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
        
        debug {  
          receive = on 
          autoreceive = on
          lifecycle = on
          event-stream = on
          unhandled = on
        }
    }
    remote {
        #this is the new upcoming remoting support, which enables multiple transports
        helios.tcp {
            transport-class = ""Akka.Remote.Transport.Helios.HeliosTcpTransport, Akka.Remote""
		    applied-adapters = []
		    transport-protocol = tcp
		    port = 0
            hostname = 0.0.0.0
		    public-hostname = ""127.0.0.1""
        }
        log-remote-lifecycle-events = INFO
    }
}
");
            using (var system = ActorSystem.Create("MyClient",config)) 
            {
                var chatClient = system.ActorOf(Props.Create<ChatClientActor>());
                var tmp = system.ActorSelection("akka.tcp://MyServer@localhost:8081/user/ChatServer");
                chatClient.Tell(new ConnectRequest()
                {
                    Username = "Roggan",
                });

                while (true)
                {
                    var input = Console.ReadLine();
                    if (input.StartsWith("*"))
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        for (int i = 0; i < 200; i++)
                        {
                            tmp.Tell(new Disconnect());
                        }
                        sw.Stop();
                        Console.WriteLine(sw.Elapsed);
                    }
                    else if (input.StartsWith("/"))
                    {
                        var parts = input.Split(' ');
                        var cmd = parts[0].ToLowerInvariant();
                        var rest = string.Join(" ",parts.Skip(1));

                        if (cmd == "/nick")
                        {
                            chatClient.Tell(new NickRequest
                            {
                                NewUsername = rest
                            });
                        }                        
                    }
                    else
                    {
                        chatClient.Tell(new SayRequest()
                        {
                            Text = input,
                        });
                    }
                }
            }
        }
    }

    class ChatClientActor : TypedActor,
        IHandle<ConnectRequest>,
        IHandle<ConnectResponse>,
        IHandle<NickRequest>,
        IHandle<NickResponse>,
        IHandle<SayRequest>,
        IHandle<SayResponse>, ILogReceive
    {
        LoggingAdapter log = Logging.GetLogger(Context);

        public ChatClientActor()
        {
            log.Error("Testing the logging feature!");
        }

        private string nick = "Roggan";
        private ActorSelection server = Context.ActorSelection("akka.tcp://MyServer@localhost:8081/user/ChatServer");
        
        public void Handle(ConnectResponse message)
        {
            Console.WriteLine("Connected!");
            Console.WriteLine(message.Message);         
        }

        public void Handle(NickRequest message)
        {
            message.OldUsername = this.nick;
            Console.WriteLine("Changing nick to {0}", message.NewUsername);
            this.nick = message.NewUsername;
            server.Tell(message);
        }

        public void Handle(NickResponse message)
        {
            Console.WriteLine("{0} is now known as {1}", message.OldUsername, message.NewUsername);
        }

        public void Handle(SayResponse message)
        {
            Console.WriteLine("{0}: {1}", message.Username, message.Text);
        }

        public void Handle(ConnectRequest message)
        {
            Console.WriteLine("Connecting....");
            server.Tell(message);
        }

        public void Handle(SayRequest message)
        {
            message.Username = this.nick;
            server.Tell(message);
        }     
    }
}