﻿/*
 *  The ircd.net project is an IRC deamon implementation for the .NET Plattform
 *  It should run on both .NET and Mono
 *  
 * Copyright (c) 2009-2017, Thomas Bruderer, apophis@apophis.ch All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 * * Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 *   
 * * Redistributions in binary form must reproduce the above copyright notice,
 *   this list of conditions and the following disclaimer in the documentation
 *   and/or other materials provided with the distribution.
 *
 * * Neither the name of ArithmeticParser nor the names of its
 *   contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IrcD.Channel;
using IrcD.Commands;
using IrcD.Commands.Arguments;
using IrcD.Core.Utils;
using IrcD.Modes;
using IrcD.Modes.ChannelModes;
using IrcD.Modes.ChannelRanks;
using IrcD.Modes.UserModes;
using IrcD.ServerReplies;
using IrcD.Tools;
using Mode = IrcD.Commands.Mode;
using Version = IrcD.Commands.Version;

namespace IrcD.Core
{

    public class IrcDaemon
    {
        public const string ServerCrLf = "\r\n";
        const char PrefixCharacter = ':';
        const int MaxBufferSize = 2048;

        // Main Datastructures
        public Dictionary<Socket, UserInfo> Sockets { get; } = new Dictionary<Socket, UserInfo>();

        public Dictionary<string, ChannelInfo> Channels { get; } = new Dictionary<string, ChannelInfo>();

        public Dictionary<char, ChannelType> SupportedChannelTypes { get; } = new Dictionary<char, ChannelType>();

        public Dictionary<string, UserInfo> Nicks { get; } = new Dictionary<string, UserInfo>();

        #region Modes

        public ModeFactory ModeFactory { get; }

        public RankList SupportedRanks { get; }

        public ChannelModeList SupportedChannelModes { get; }
        public UserModeList SupportedUserModes { get; }

        #endregion

        // Protocol
        public CommandList Commands { get; }

        public ServerReplies.ServerReplies Replies { get; }

        public List<string> Capabilities { get; private set; }

        private bool _connected;
        private bool _restart;

        private readonly byte[] _buffer = new byte[MaxBufferSize];
        private EndPoint _ep = new IPEndPoint(0, 0);
        private EndPoint? _localEndPoint;

        public ServerOptions Options { get; }

        public ServerStats Stats { get; }

        public string ServerPrefix => PrefixCharacter + Options.ServerName;

        public DateTime ServerCreated { get; }

        #region Events
        public event EventHandler<RehashEventArgs>? ServerRehash;
        internal void OnRehashEvent(object sender, RehashEventArgs e)
        {
            ServerRehash?.Invoke(sender, e);
        }

        #endregion

        public IrcDaemon(IrcMode ircMode = IrcMode.Modern)
        {
            Capabilities = new List<string>();

            // Create Optionobject & Set the proper IRC Protocol Version
            // The protocol version cannot be changed after construction,
            // because the construction methods below use this Option
            Options = new ServerOptions(ircMode);

            //Clean Interface to statistics, it needs the IrcDaemon Object to gather this information.
            Stats = new ServerStats(this);

            // Setup Modes Infrastructure
            ModeFactory = new ModeFactory();
            SupportedChannelModes = new ChannelModeList(this);
            SupportedRanks = new RankList(this);
            SupportedUserModes = new UserModeList(this);

            // The Protocol Objects
            Commands = new CommandList(this);
            Replies = new ServerReplies.ServerReplies(this);
            ServerCreated = DateTime.Now;

            // Add Commands
            SetupCommands();
            // Add Modes
            SetupModes();
            //Add ChannelTypes
            SetupChannelTypes();
        }

        protected virtual void SetupCommands()
        {
            Commands.Add(new Admin(this));
            Commands.Add(new Away(this));
            Commands.Add(new Connect(this));
            Commands.Add(new Die(this));
            Commands.Add(new Error(this));
            Commands.Add(new Info(this));
            Commands.Add(new Invite(this));
            Commands.Add(new IsOn(this));
            Commands.Add(new Join(this));
            Commands.Add(new Kick(this));
            Commands.Add(new Kill(this));
            Commands.Add(new Links(this));
            Commands.Add(new List(this));
            Commands.Add(new ListUsers(this));
            Commands.Add(new MessageOfTheDay(this));
            Commands.Add(new Mode(this));
            Commands.Add(new Names(this));
            Commands.Add(new Nick(this));
            Commands.Add(new Notice(this));
            Commands.Add(new Oper(this));
            Commands.Add(new Part(this));
            Commands.Add(new Pass(this));
            Commands.Add(new Ping(this));
            Commands.Add(new Pong(this));
            Commands.Add(new PrivateMessage(this));
            Commands.Add(new Quit(this));
            Commands.Add(new Rehash(this));
            Commands.Add(new Restart(this));
            Commands.Add(new Server(this));
            Commands.Add(new ServerQuit(this));
            Commands.Add(new Service(this));
            Commands.Add(new Stats(this));
            Commands.Add(new Summon(this));
            Commands.Add(new Time(this));
            Commands.Add(new Topic(this));
            Commands.Add(new Trace(this));
            Commands.Add(new User(this));
            Commands.Add(new UserHost(this));
            Commands.Add(new Version(this));
            Commands.Add(new Wallops(this));
            Commands.Add(new Who(this));
            Commands.Add(new WhoIs(this));
            Commands.Add(new WhoWas(this));

            if (Options.IrcMode == IrcMode.Rfc2810 || Options.IrcMode == IrcMode.Modern)
            {
                Commands.Add(new ServiceList(this));
                Commands.Add(new ServiceQuery(this));
            }

            if (Options.IrcMode == IrcMode.Modern)
            {
                Commands.Add(new Capabilities(this));
                Commands.Add(new Knock(this));
                Commands.Add(new Language(this));
                Commands.Add(new Silence(this));
            }
        }

        protected virtual void SetupModes()
        {
            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeBan>());
            if (Options.IrcMode == IrcMode.Rfc2810 || Options.IrcMode == IrcMode.Modern)
                SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeBanException>());

            if (Options.IrcMode == IrcMode.Modern)
                SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeColorless>());

            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeInvite>());
            if (Options.IrcMode == IrcMode.Rfc2810 || Options.IrcMode == IrcMode.Modern)
                SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeInviteException>());

            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeKey>());
            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeLimit>());
            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeModerated>());
            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeNoExternal>());
            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeSecret>());
            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModePrivate>());
            SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeTopic>());
            if (Options.IrcMode == IrcMode.Modern)
                SupportedChannelModes.Add(ModeFactory.AddChannelMode<ModeTranslate>());

            if (Options.IrcMode == IrcMode.Modern) SupportedRanks.Add(ModeFactory.AddChannelRank<ModeHalfOp>());
            SupportedRanks.Add(ModeFactory.AddChannelRank<ModeOp>());
            SupportedRanks.Add(ModeFactory.AddChannelRank<ModeVoice>());

            SupportedUserModes.Add(ModeFactory.AddUserMode<ModeLocalOperator>());
            SupportedUserModes.Add(ModeFactory.AddUserMode<ModeInvisible>());
            SupportedUserModes.Add(ModeFactory.AddUserMode<ModeOperator>());
            SupportedUserModes.Add(ModeFactory.AddUserMode<ModeRestricted>());
            SupportedUserModes.Add(ModeFactory.AddUserMode<ModeWallops>());
        }

        protected virtual void SetupChannelTypes()
        {
            ChannelType chan = new NormalChannel();
            SupportedChannelTypes.Add(chan.Prefix, chan);
        }

        public void Start()
        {
            if (_connected) return;

            do
            {
                _restart = false;
                _connected = true;

                MainLoop();
            } while (_restart);
        }

        public void Stop(bool startAgain)
        {
            _connected = false;
            _restart = startAgain;
        }

        /// <summary>
        /// Start a IRC Server to Server Connection
        /// </summary>
        /// <param name="server"></param>
        /// <param name="port"></param>
        internal void Connect(string server, int port)
        {

            var ip = Dns.GetHostAddresses(server).FirstOrDefault();
            if (ip != null)
            {
                var serverEndPoint = new IPEndPoint(ip, port);
                var serverSocket = new Socket(serverEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                serverSocket.Connect(serverEndPoint);

                // sockets.Add(serverSocket, new ServerInfo(this));

            }

        }


        private void MainLoop()
        {

            foreach (var port in Options.ServerPorts)
            {
                _localEndPoint = new IPEndPoint(IPAddress.Any, port);
                var connectSocket = new Socket(_localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                connectSocket.Bind(_localEndPoint);
                connectSocket.Listen(20);

                Sockets.Add(connectSocket, new UserInfo(this, connectSocket, Options.ServerName, true, true));
            }

            while (_connected)
            {
                try
                {
                    var activeSockets = new List<Socket>(Sockets.Keys);

                    Socket.Select(activeSockets, null, null, 2000000);

                    foreach (Socket s in activeSockets)
                    {
                        try
                        {
                            if (Sockets[s].IsAcceptSocket)
                            {
                                Socket temp = s.Accept();
                                Sockets.Add(temp, new UserInfo(this, temp, ((IPEndPoint)temp.RemoteEndPoint!).Address.ToString(), false, String.IsNullOrEmpty(Options.ServerPass)));
                                Logger.Log("New Client connected!", 4, "MainLoop");
                            }
                            else
                            {
                                try
                                {
                                    _buffer.Initialize();
                                    int numBytes = s.ReceiveFrom(_buffer, ref _ep);
                                    foreach (string line in Encoding.UTF8.GetString(_buffer, 0, numBytes).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                                    {
                                        Parser(line, Sockets[s]);
                                    }
                                }
                                catch (SocketException e)
                                {
                                    Logger.Log("ERROR:  (Socket reset) " + e.Message + "(CODE:" + e.ErrorCode + ")", 4, "E1" + Sockets[s].Nick);
                                    Sockets[s].Remove("Socket reset by peer (" + e.ErrorCode + ")");

                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Log("Unknown ERROR: " + e.Message, 4, "E2" + Sockets[s].Nick);
                            Logger.Log("Trace: " + e.StackTrace);
                        }
                    }

                    // Pinger : we only ping if necessary
                    foreach (var user in from user in Sockets.Where(s => s.Value.Registered)
                                         let interval = DateTime.Now.AddMinutes(-1)
                                         where user.Value.LastAction < interval && user.Value.LastAlive < interval
                                         select user.Value)
                    {
                        if (user.LastAlive < DateTime.Now.AddMinutes(-5))
                        {
                            // Ping Timeout (5 Minutes without any life sign)
                            user.Remove("Ping Timeout");
                        }
                        else if (user.LastAlive < DateTime.Now.AddMinutes(-1) && user.LastPing < DateTime.Now.AddMinutes(-1))
                        {
                            user.LastPing = DateTime.Now;
                            Commands.Send(new PingArgument(user));
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Log("Unknown ERROR: " + e.Message);
                    Logger.Log("Trace: " + e.StackTrace);
                }

            }

            // QUIT Server
            foreach (var user in Sockets.Values.Where(u => !u.IsAcceptSocket).ToArray())
            {
                user.Remove("Server Shutdown");
            }

            foreach (var serverSocket in Sockets.Values)
            {
                serverSocket.Socket.Close(5);
            }

            Sockets.Clear();
            Channels.Clear();
            Nicks.Clear();
            GC.Collect();
        }

        private void Parser(string line, UserInfo info)
        {

#if DEBUG
            Logger.Log(line, location: "IN:" + info.Nick);
#endif

            if (line.Length > Options.MaxLineLength)
            {
                return;  // invalid message
            }

            string? prefix = null;
            string? command = null;
            var replyCode = ReplyCode.Null;
            var args = new List<string>();

            try
            {
                int i = 0;
                /* This runs in the mainloop :: parser needs to return fast
                 * -> nothing which could block it may be called inside Parser
                 * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */
                if (line[0] == PrefixCharacter)
                {
                    /* we have a prefix */
                    while (line[++i] != ' ') { }

                    prefix = line.Substring(1, i - 1);
                }
                else
                {
                    prefix = info.Usermask;
                }

                int commandStart = i;
                /*command might be numeric (xxx) or command */
                if (char.IsDigit(line[i + 1]) && char.IsDigit(line[i + 2]) && char.IsDigit(line[i + 3]))
                {
                    replyCode = (ReplyCode)int.Parse(line.Substring(i + 1, 3));
                    i += 4;
                }
                else
                {
                    while ((i < (line.Length - 1)) && line[++i] != ' ') { }

                    if (line.Length - 1 == i) { ++i; }
                    command = line.Substring(commandStart, i - commandStart);
                }

                ++i;
                int paramStart = i;
                while (i < line.Length)
                {
                    if (line[i] == ' ' && i != paramStart)
                    {
                        args.Add(line.Substring(paramStart, i - paramStart));
                        paramStart = i + 1;
                    }
                    if (line[i] == PrefixCharacter)
                    {
                        if (paramStart != i)
                        {
                            args.Add(line.Substring(paramStart, i - paramStart));
                        }
                        args.Add(line.Substring(i + 1));
                        break;
                    }

                    ++i;
                }

                if (i == line.Length)
                {
                    args.Add(line.Substring(paramStart));
                }

            }
            catch (IndexOutOfRangeException)
            {
                Logger.Log("Invalid Message: " + line);
                // invalid message
            }

            if (command == null)
                return;

            FilterArgs(args);
            if (replyCode == ReplyCode.Null)
            {
                Commands.Handle(info, prefix ?? "", command, args, line.Length);
            }
            else
            {
                Commands.Handle(info, prefix ?? "", replyCode, args, line.Length);
            }
        }


        private static void FilterArgs(List<string> args)
        {
            args.RemoveAll(s => string.IsNullOrEmpty(s.Trim()));
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="nick"></param>
        /// <returns></returns>
        public bool ValidNick(string nick)
        {
            if (nick.Length > Options.MaxNickLength)
                return false;

            if (Options.IrcMode == IrcMode.Modern)
            {
                if (nick.Any(c => c == ' ' || c == ',' || c == '\x7' || c == '!' || c == '@' || c == '*' || c == '?' || c == '+' || c == '%' || c == '#'))
                    return false;
            }

            if (Options.IrcMode == IrcMode.Rfc1459 || Options.IrcMode == IrcMode.Rfc2810)
            {
                if (!nick.All(c => (c >= '\x5B' && c <= '\x60') || (c >= '\x7B' && c <= '\x7D') || (c >= 'a' && c < 'z') || (c >= 'A' && c < 'Z') || (c >= '0' && c < '9')
                                    || c == '[' || c == ']' || c == '\\' || c == '`' || c == '_' || c == '^' || c == '{' || c == '|' || c == '}'))
                    return false;
            }

            return true;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public bool ValidChannel(string channel)
        {
            return SupportedChannelTypes.Any(t => t.Value.Prefix == channel[0]) && (!channel.Any(c => c == ' ' || c == ',' || c == '\x7' || c == ':'));
        }

        public void ConnectFromServer(UserInfo info)
        {
            //
        }

        public void ConnectToServer()
        {
            //
        }

    }
}
