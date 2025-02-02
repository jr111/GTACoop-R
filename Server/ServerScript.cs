﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Lidgren.Network;

namespace CoopServer
{
    public abstract class ServerScript
    {
        public API API { get; } = new();
    }

    internal class Resource
    {
        public bool ReadyToStop = false;

        private static Thread _mainThread;
        private static Queue _actionQueue;
        private static ServerScript _script;

        public Resource(ServerScript script)
        {
            _actionQueue = Queue.Synchronized(new Queue());
            _mainThread = new(ThreadLoop) { IsBackground = true };
            _mainThread.Start();

            lock (_actionQueue.SyncRoot)
            {
                _actionQueue.Enqueue(() =>
                {
                    _script = script;
                    _script.API.InvokeStart();
                });
            }
        }

        private void ThreadLoop()
        {
            while (!Program.ReadyToStop)
            {
                Queue localQueue;
                lock (_actionQueue.SyncRoot)
                {
                    localQueue = new(_actionQueue);
                    _actionQueue.Clear();
                }

                while (localQueue.Count > 0)
                {
                    (localQueue.Dequeue() as Action)?.Invoke();
                }

                // 16 milliseconds to sleep to reduce CPU usage
                Thread.Sleep(1000 / 60);
            }

            _script.API.InvokeStop();
            ReadyToStop = true;
        }

        public bool InvokeModPacketReceived(long from, long target, string mod, byte customID, byte[] bytes)
        {
            Task<bool> task = new(() => _script.API.InvokeModPacketReceived(from, target, mod, customID, bytes));
            task.Start();
            task.Wait(5000);

            return task.Result;
        }

        public void InvokePlayerHandshake(Client client)
        {
            lock (_actionQueue.SyncRoot)
            {
                _actionQueue.Enqueue(new Action(() => _script.API.InvokePlayerHandshake(client)));
            }
        }

        public void InvokePlayerConnected(Client client)
        {
            lock (_actionQueue.SyncRoot)
            {
                _actionQueue.Enqueue(new Action(() => _script.API.InvokePlayerConnected(client)));
            }
        }

        public void InvokePlayerDisconnected(Client client)
        {
            lock (_actionQueue.SyncRoot)
            {
                _actionQueue.Enqueue(new Action(() => _script.API.InvokePlayerDisconnected(client)));
            }
        }

        public bool InvokeChatMessage(string username, string message)
        {
            Task<bool> task = new(() => _script.API.InvokeChatMessage(username, message));
            task.Start();
            task.Wait(5000);

            return task.Result;
        }

        public void InvokePlayerPositionUpdate(PlayerData playerData)
        {
            lock (_actionQueue.SyncRoot)
            {
                _actionQueue.Enqueue(new Action(() => _script.API.InvokePlayerPositionUpdate(playerData)));
            }
        }

        public void InvokePlayerUpdate(Client client)
        {
            lock (_actionQueue.SyncRoot)
            {
                _actionQueue.Enqueue(new Action(() => _script.API.InvokePlayerUpdate(client)));
            }
        }

        public void InvokePlayerHealthUpdate(PlayerData playerData)
        {
            lock (_actionQueue.SyncRoot)
            {
                _actionQueue.Enqueue(new Action(() => _script.API.InvokePlayerHealthUpdate(playerData)));
            }
        }
    }

    public class API
    {
        #region DELEGATES
        public delegate void EmptyEvent();
        public delegate void ChatEvent(string username, string message, CancelEventArgs cancel);
        public delegate void PlayerEvent(Client client);
        public delegate void ModEvent(long from, long target, string mod, byte customID, byte[] bytes, CancelEventArgs args);
        #endregion

        #region EVENTS
        /// <summary>
        /// Called when the server has started
        /// </summary>
        public event EmptyEvent OnStart;
        /// <summary>
        /// Called when the server has stopped
        /// </summary>
        public event EmptyEvent OnStop;
        /// <summary>
        /// Called when the server receives a new chat message for players
        /// </summary>
        public event ChatEvent OnChatMessage;
        /// <summary>
        /// Called when the server receives a new incoming connection
        /// </summary>
        public event PlayerEvent OnPlayerHandshake;
        /// <summary>
        /// Called when a new player has successfully connected
        /// </summary>
        public event PlayerEvent OnPlayerConnected;
        /// <summary>
        /// Called when a new player has successfully disconnected
        /// </summary>
        public event PlayerEvent OnPlayerDisconnected;
        /// <summary>
        /// Called when a new player sends data like health
        /// </summary>
        public event PlayerEvent OnPlayerUpdate;
        /// <summary>
        /// Called when a player has a new health value
        /// </summary>
        public event PlayerEvent OnPlayerHealthUpdate;
        /// <summary>
        /// Called when a player has a new position
        /// </summary>
        public event PlayerEvent OnPlayerPositionUpdate;
        /// <summary>
        /// Called when a player sends a packet from another modification
        /// </summary>
        public event ModEvent OnModPacketReceived;

        internal void InvokeStart()
        {
            OnStart?.Invoke();
        }

        internal void InvokeStop()
        {
            OnStop?.Invoke();
        }

        internal void InvokePlayerHandshake(Client client)
        {
            OnPlayerHandshake?.Invoke(client);
        }

        internal void InvokePlayerConnected(Client client)
        {
            OnPlayerConnected?.Invoke(client);
        }

        internal void InvokePlayerDisconnected(Client client)
        {
            OnPlayerDisconnected?.Invoke(client);
        }

        internal void InvokePlayerUpdate(Client client)
        {
            OnPlayerUpdate?.Invoke(client);
        }

        internal void InvokePlayerHealthUpdate(PlayerData playerData)
        {
            OnPlayerHealthUpdate?.Invoke(Server.Clients.First(x => x.Player.Username == playerData.Username));
        }

        internal bool InvokeChatMessage(string username, string message)
        {
            CancelEventArgs args = new(false);
            OnChatMessage?.Invoke(username, message, args);
            return args.Cancel;
        }

        internal void InvokePlayerPositionUpdate(PlayerData playerData)
        {
            OnPlayerPositionUpdate?.Invoke(Server.Clients.First(x => x.Player.Username == playerData.Username));
        }

        internal bool InvokeModPacketReceived(long from, long target, string mod, byte customID, byte[] bytes)
        {
            CancelEventArgs args = new(false);
            OnModPacketReceived?.Invoke(from, target, mod, customID, bytes, args);
            return args.Cancel;
        }
        #endregion

        #region FUNCTIONS
        /// <summary>
        /// Send a mod packet to all players
        /// </summary>
        /// <param name="mod">The name of the modification that will receive the data</param>
        /// <param name="customID">The ID to check what this data is</param>
        /// <param name="bytes">The serialized data</param>
        /// <param name="netHandleList">The list of connections (players) that will receive the data</param>
        public static void SendModPacketToAll(string mod, byte customID, byte[] bytes, List<long> netHandleList = null)
        {
            try
            {
                List<NetConnection> connections = netHandleList == null
                    ? Server.MainNetServer.Connections
                    : Server.MainNetServer.Connections.FindAll(c => netHandleList.Contains(c.RemoteUniqueIdentifier));

                NetOutgoingMessage outgoingMessage = Server.MainNetServer.CreateMessage();
                new ModPacket()
                {
                    NetHandle = 0,
                    Target = 0,
                    Mod = mod,
                    CustomPacketID = customID,
                    Bytes = bytes
                }.PacketToNetOutGoingMessage(outgoingMessage);
                Server.MainNetServer.SendMessage(outgoingMessage, connections, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Mod);
                Server.MainNetServer.FlushSendQueue();
            }
            catch (Exception e)
            {
                Logging.Error($">> {e.Message} <<>> {e.Source ?? string.Empty} <<>> {e.StackTrace ?? string.Empty} <<");
            }
        }

        /// <summary>
        /// Send a native call (Function.Call) to all players
        /// </summary>
        /// <param name="hash">The hash (Example: 0x25223CA6B4D20B7F = GET_CLOCK_HOURS)</param>
        /// <param name="args">The arguments (Example: "Function.Call(Hash.SET_TIME_SCALE, args);")</param>
        public static void SendNativeCallToAll(ulong hash, params object[] args)
        {
            try
            {
                if (Server.MainNetServer.ConnectionsCount == 0)
                {
                    return;
                }

                List<NativeArgument> arguments = Util.ParseNativeArguments(args);
                if (arguments == null)
                {
                    Logging.Error($"[ServerScript->SendNativeCallToAll(ulong hash, params object[] args)]: One or more arguments do not exist!");
                    return;
                }

                NativeCallPacket packet = new()
                {
                    Hash = hash,
                    Args = arguments
                };

                NetOutgoingMessage outgoingMessage = Server.MainNetServer.CreateMessage();
                packet.PacketToNetOutGoingMessage(outgoingMessage);
                Server.MainNetServer.SendMessage(outgoingMessage, Server.MainNetServer.Connections, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Native);
            }
            catch (Exception e)
            {
                Logging.Error($">> {e.Message} <<>> {e.Source ?? string.Empty} <<>> {e.StackTrace ?? string.Empty} <<");
            }
        }

        /// <summary>
        /// Get all connections as a list of NetHandle(long)
        /// </summary>
        /// <returns></returns>
        public static List<long> GetAllConnections()
        {
            List<long> result = new();

            Server.MainNetServer.Connections.ForEach(x => result.Add(x.RemoteUniqueIdentifier));

            return result;
        }

        public static int GetAllClientsCount()
        {
            return Server.Clients.Count;
        }

        public static List<Client> GetAllClients()
        {
            return Server.Clients;
        }

        public static Client GetClientByUsername(string username)
        {
            return Server.Clients.Find(x => x.Player.Username.ToLower() == username.ToLower());
        }

        /// <summary>
        /// Send a chat message to all players
        /// </summary>
        /// <param name="message">The chat message</param>
        /// <param name="username">The username which send this message (default = "Server")</param>
        /// <param name="netHandleList">The list of connections (players) who received this chat message</param>
        public static void SendChatMessageToAll(string message, string username = "Server", List<long> netHandleList = null)
        {
            try
            {
                if (Server.MainNetServer.ConnectionsCount == 0)
                {
                    return;
                }

                List<NetConnection> connections = netHandleList == null
                    ? Server.MainNetServer.Connections
                    : Server.MainNetServer.Connections.FindAll(c => netHandleList.Contains(c.RemoteUniqueIdentifier));

                NetOutgoingMessage outgoingMessage = Server.MainNetServer.CreateMessage();
                new ChatMessagePacket()
                {
                    Username = username,
                    Message = message
                }.PacketToNetOutGoingMessage(outgoingMessage);
                Server.MainNetServer.SendMessage(outgoingMessage, connections, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Chat);
            }
            catch (Exception e)
            {
                Logging.Error($">> {e.Message} <<>> {e.Source ?? string.Empty} <<>> {e.StackTrace ?? string.Empty} <<");
            }
        }

        /// <summary>
        /// Register a new command chat command (Example: "/test")
        /// </summary>
        /// <param name="name">The name of the command (Example: "test" for "/test")</param>
        /// <param name="usage">How to use this message (argsLength required!)</param>
        /// <param name="argsLength">The length of args (Example: "/message USERNAME MESSAGE" = 2) (usage required!)</param>
        /// <param name="callback">Create a new function!</param>
        public static void RegisterCommand(string name, string usage, short argsLength, Action<CommandContext> callback)
        {
            Server.RegisterCommand(name, usage, argsLength, callback);
        }
        /// <summary>
        /// Register a new command chat command (Example: "/test")
        /// </summary>
        /// <param name="name">The name of the command (Example: "test" for "/test")</param>
        /// <param name="callback">Create a new function!</param>
        public static void RegisterCommand(string name, Action<CommandContext> callback)
        {
            Server.RegisterCommand(name, callback);
        }

        /// <summary>
        /// Register a class of commands
        /// </summary>
        /// <typeparam name="T">The name of your class with functions</typeparam>
        public static void RegisterCommands<T>()
        {
            Server.RegisterCommands<T>();
        }
        #endregion
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class Command : Attribute
    {
        /// <summary>
        /// Sets name of the command
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Set the Usage (Example: "Please use "/help"". ArgsLength required!)
        /// </summary>
        public string Usage { get; set; }

        /// <summary>
        /// Set the length of arguments (Example: 2 for "/message USERNAME MESSAGE". Usage required!)
        /// </summary>
        public short ArgsLength { get; set; }

        public Command(string name)
        {
            Name = name;
        }
    }

    public class CommandContext
    {
        /// <summary>
        /// Gets the client which executed the command
        /// </summary>
        public Client Client { get; internal set; }

        /// <summary>
        /// Gets the arguments (Example: "/message USERNAME MESSAGE", Args[0] for USERNAME)
        /// </summary>
        public string[] Args { get; internal set; }
    }
}
