﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using ChatroomServer.ClientPackets;
using ChatroomServer.Packets;
using ChatroomServer.ServerPackets;

#nullable enable
namespace ChatroomServer
{
    /// <summary>
    /// A chatroom server instance.
    /// </summary>
    public class Server : IDisposable
    {
        public Logger? Logger;
        private readonly HashSet<string> usedNames = new HashSet<string>();
        private readonly Dictionary<byte, ClientInfo> clients = new Dictionary<byte, ClientInfo>();
        private readonly Dictionary<byte, TcpClient> newClients = new Dictionary<byte, TcpClient>();
        private readonly TcpListener tcpListener;
        private readonly ServerConfig config;
        private readonly Queue<(string Name, ReceiveMessagePacket StoredMessage)> recallableMessages = new Queue<(string Name, ReceiveMessagePacket StoredMessage)>();

        private byte recentID = 1;

        /// <summary>
        /// Initializes a new instance of the <see cref="Server"/> class.
        /// </summary>
        /// <param name="port">The port on which to start the server.</param>
        /// <param name="config">The configuration of the server.</param>
        public Server(int port, ServerConfig config)
        {
            tcpListener = new TcpListener(System.Net.IPAddress.Any, port);
            this.config = config;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Server"/> class.
        /// </summary>
        /// <param name="port">The port on which to start the server.</param>
        /// <param name="config">The configuration of the server.</param>
        /// <param name="logger">The logger with which to log information.</param>
        public Server(int port, ServerConfig config, Logger logger)
        {
            tcpListener = new TcpListener(System.Net.IPAddress.Any, port);
            this.Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.config = config;
        }

        /// <summary>
        /// Starts the server. Should only be called once.
        /// </summary>
        /// <exception cref="SocketException">Port unavailable, or otherwise.</exception>
        public void Start()
        {
            tcpListener.Start();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (var item in clients)
            {
                item.Value.TcpClient?.Dispose();
            }
        }

        /// <summary>
        /// Reads available data from clients, and responds accordingly.
        /// Should be called repeatedly.
        /// </summary>
        public void Update()
        {
            // Check pending clients
            while (tcpListener.Pending())
            {
                // Accept pending client.
                TcpClient client = tcpListener.AcceptTcpClient();
                NetworkStream stream = client.GetStream();

                // If lobby is full, turn away client.
                if (!(GetNextUserID() is byte userID))
                {
                    client.Close();
                    Logger?.Warning("Lobby full");
                    return;
                }

                // Assign client their ID
                stream.Write(new SendUserIDPacket(userID).Serialize());

                ClientInfo clientInfo = new ClientInfo(client);
                clients.Add(userID, clientInfo);

                Logger?.Info($"{client.Client.RemoteEndPoint} connected: ID {userID}");
            }

            newClients.Clear();

            // Pings clients
            foreach (var client in clients)
            {
                // Ping client if more time than whats good, has passed.
                long timeDifference = (long)(DateTime.UtcNow - client.Value.LastActiveUTCTime).TotalMilliseconds;

                // Client hasn't handshaked yet.
                if (client.Value.Name is null)
                {
                    if (timeDifference <= config.HandshakeTimeout)
                    {
                        continue;
                    }

                    Logger?.Info($"Client handshake timed out: {client.Key}");
                    DisconnectClient(client.Key);

                    continue;
                }

                if (timeDifference <= config.MaxTimeSinceLastActive)
                {
                    continue;
                }

                SendPacket(client.Key, new PingPacket());
            }

            // Check for new packets from clients and parses them
            foreach (var client in clients)
            {
                try
                {
                    NetworkStream stream = client.Value.TcpClient.GetStream();

                    if (!stream.DataAvailable)
                    {
                        continue;
                    }

                    // Refresh last active.
                    client.Value.UpdateLastActiveTime();

                    byte packetID = (byte)stream.ReadByte();

                    if (!Enum.IsDefined(typeof(ClientPacketType), packetID))
                    {
                        // Unknown packet: Everything is fine!
                        Logger?.Warning($"Received unknown packet from {client.Key}");
                        continue;
                    }

                    var packetType = (ClientPacketType)packetID;

                    OnPacketReceived(stream, packetType, client.Key, client.Value);
                }
                catch (InvalidOperationException ex)
                {
                    Logger?.Warning($"Client disconnect by invalid operation: {ex}");
                    DisconnectClient(client.Key);
                }
                catch (SocketException ex)
                {
                    Logger?.Debug($"Client disconnected by Socket exception: {ex}");
                    DisconnectClient(client.Key);
                }
                catch (Exception ex)
                {
                    Logger?.Error(ex.ToString());
                    throw;
                }
            }
        }

        private static long GetUnixTime() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private bool IsNameValid(string name)
        {
            // TODO: Check length.
            // TODO: Check for invalid characters.

            // Is name taken
            if (usedNames.Contains(name))
            {
                return false;
            }

            return true;
        }

        private string FixName(string name)
        {
            string newName = name;
            int nextIndex = 2;
            while (!IsNameValid(newName))
            {
                newName = $"{name} ({nextIndex++})";
            }

            return newName;
        }

        /// <summary>
        /// Handles incoming packet.
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="packetType"></param>
        /// <param name="clientID"></param>
        /// <param name="client"></param>
        private void OnPacketReceived(NetworkStream stream, ClientPacketType packetType, byte clientID, ClientInfo client)
        {
            // Parse and handle the packets ChangeNamePacket and SendMessagePacket,
            // by responding to all other clients with a message or a userinfo update
            switch (packetType)
            {
                case ClientPacketType.ChangeName:
                    var changeNamePacket = new ChangeNamePacket(stream);

                    string? oldName = client.Name;

                    if (!(oldName is null))
                    {
                        usedNames.Remove(oldName);
                    }

                    client.Name = FixName(changeNamePacket.Name);
                    usedNames.Add(client.Name);

                    // First time connecting
                    if (oldName is null)
                    {
                        OnClientHandshakeFinished(clientID);

                        Logger?.Info("User connected: " + client.Name);
                        ServerLogAll($"{client.Name} forbandt!");
                    }
                    else
                    {
                        Logger?.Info($"User {clientID} name updated from {oldName} to {client.Name}");
                        ServerLogAll($"{oldName} skiftede navn til {client.Name}");
                    }

                    SendPacketAll(new SendUserInfoPacket(clientID, client.Name));
                    break;
                case ClientPacketType.SendMessage:
                    var sendMessagePacket = new SendMessagePacket(stream);

                    // Ignore packet if client handshake hasn't finished.
                    if (client.Name is null)
                    {
                        break;
                    }

                    ReceiveMessagePacket responsePacket = new ReceiveMessagePacket(
                        clientID,
                        sendMessagePacket.TargetUserID,
                        GetUnixTime(),
                        sendMessagePacket.Message);

                    // Debug display chatmessage.
                    if (!(Logger is null))
                    {
                        DebugDisplayChatMessage(clientID, sendMessagePacket.TargetUserID, sendMessagePacket.Message);
                    }

                    // Store chatmessage if public.
                    if (sendMessagePacket.TargetUserID == 0)
                    {
                        recallableMessages.Enqueue((client.Name, responsePacket));

                        // Shorten recallable message queue to configuration
                        while (recallableMessages.Count > config.MaxStoredMessages)
                        {
                            recallableMessages.Dequeue();
                        }

                        SendPacketAll(responsePacket);
                    }
                    else
                    {
                        SendPacket(clientID, responsePacket);

                        if (!clients.TryGetValue(sendMessagePacket.TargetUserID, out ClientInfo targetClient))
                        {
                            Logger?.Warning("Target user ID not found: " + sendMessagePacket.TargetUserID);
                            break;
                        }

                        SendPacket(sendMessagePacket.TargetUserID, responsePacket);
                    }

                    break;
                case ClientPacketType.Disconnect:
                    Logger?.Info($"Client: {client.Name} has disconnected");

                    ServerLogAll($"{client.Name} forsvandt!");
                    DisconnectClient(clientID);
                    break;
                default:
                    break;
            }
        }

        private void ServerLog(byte userID, string serverMessage)
        {
            SendPacket(userID, new LogMessagePacket(GetUnixTime(), serverMessage));
        }

        private void ServerLogAll(string serverMessage)
        {
            SendPacketAll(new LogMessagePacket(GetUnixTime(), serverMessage));
        }

        private void DebugDisplayChatMessage(byte authorID, byte targetID, string chatMsg)
        {
            const int minimumDisplayedMessage = 15;

            StringBuilder outputMsg = new StringBuilder();
            outputMsg.Append($"User {authorID} messaged {targetID}: ");

            int messageLength = Math.Min(
                chatMsg.Length,
                Math.Max(
                    Console.BufferWidth - outputMsg.Length - 7,
                    minimumDisplayedMessage));

            outputMsg.Append(chatMsg.Substring(0, messageLength));

            if (messageLength < chatMsg.Length)
            {
                outputMsg.Append("[...]");
            }

            Logger?.Info(outputMsg.ToString());
        }

        private void OnClientHandshakeFinished(byte clientID)
        {
            // Recall messages
            var messagesToRecall = new Queue<(string Name, ReceiveMessagePacket)>(recallableMessages);

            // Collection of unique user information.
            HashSet<(byte, string)> updatedUserinfo = new HashSet<(byte, string)>();

            while (messagesToRecall.Count > 0)
            {
                (string authorname, ReceiveMessagePacket receiveMessagePacket) = messagesToRecall.Dequeue();

                if (updatedUserinfo.Add((receiveMessagePacket.UserID, authorname)))
                {
                    // Send UpdateUserInfo
                    SendPacket(clientID, new SendUserInfoPacket(receiveMessagePacket.UserID, authorname));
                }

                SendPacket(clientID, receiveMessagePacket);
            }

            // Disconnect absent people
            foreach ((byte, string) item in updatedUserinfo)
            {
                // Only disconnect people not present anymore.
                if (clients.ContainsKey(item.Item1))
                {
                    continue;
                }

                SendPacket(clientID, new UserLeftPacket(item.Item1));
            }

            // Send missing current user information
            foreach (KeyValuePair<byte, ClientInfo> otherClientPair in clients)
            {
                byte otherClientID = otherClientPair.Key;
                string? otherClientName = otherClientPair.Value.Name;

                // A client that is just connecting.
                if (otherClientName is null)
                {
                    continue;
                }

                // Client name has already been sent.
                if (updatedUserinfo.Contains((otherClientID, otherClientName)))
                {
                    continue;
                }

                SendPacket(clientID, new SendUserInfoPacket(otherClientID, otherClientName));
            }

            // Message of the day
            if (!(config.MessageOfTheDay is null))
            {
                ServerLog(clientID, config.MessageOfTheDay);
            }
        }

        private byte? GetNextUserID()
        {
            byte startID = recentID;
            if (recentID == byte.MaxValue)
            {
                // Søg forfra.
                startID = 1;
            }

            for (byte i = startID; i < byte.MaxValue; i++)
            {
                if (!clients.ContainsKey(i) && !newClients.ContainsKey(i))
                {
                    recentID++;
                    return i;
                }
            }

            return null;
        }

        private void DisconnectClient(byte userID)
        {
            Logger?.Info($"Disconnected ID: {userID}");

            if (!clients.TryGetValue(userID, out ClientInfo clientInfo))
            {
                Logger?.Warning($"Trying to disconnect removed client: {userID}");
                return;
            }

            if (!(clientInfo.Name is null))
            {
                usedNames.Remove(clientInfo.Name);
            }

            clientInfo.TcpClient?.Close();

            // Remove client.
            clients.Remove(userID);

            // Send UserLeftPacket to all clients iteratively.
            SendPacketAll(new UserLeftPacket(userID), userID);
        }

        private void SendPacket<T>(byte userID, T packet)
            where T : ServerPacket
        {
            clients[userID].UpdateLastActiveTime();
            byte[] serializedPacket = packet.Serialize();

            try
            {
                NetworkStream stream = clients[userID].TcpClient.GetStream();
                stream.Write(serializedPacket, 0, serializedPacket.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                // Disconnect client because it isn't connected.
                DisconnectClient(userID);
            }
        }

        private void SendPacketAll<T>(T packet, byte exceptUser = 0)
            where T : ServerPacket
        {
            foreach (var client in clients)
            {
                if (client.Key == exceptUser)
                {
                    continue;
                }

                if (client.Value.Name is null)
                {
                    continue;
                }

                SendPacket(client.Key, packet);
            }
        }

        /*
        private void SendPacket(byte userID, ClientInfo client, byte[] data)
        {
            client.UpdateLastActiveTime();

            if (data[0] != 1)
            {
                Logger?.Debug("Sender pakke: " + Enum.GetName(typeof(ServerPacketType), (ServerPacketType)data[0]));
            }

            try
            {
                NetworkStream stream = client.TcpClient.GetStream();
                stream.Write(data, 0, data.Length);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                // Disconnect client because it isn't connected.
                DisconnectClient(userID);
            }
        }

        private void SendPacketAll(byte[] data, byte exceptUser = 0)
        {
            foreach (var client in clients)
            {
                if (client.Key == exceptUser)
                {
                    continue;
                }

                if (client.Value.Name is null)
                {
                    continue;
                }

                SendPacket(client.Key, client.Value, data);
            }
        }*/
    }
}
