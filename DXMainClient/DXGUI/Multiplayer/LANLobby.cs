﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClientCore;
using ClientCore.CnCNet5;
using ClientCore.Extensions;
using ClientGUI;
using DTAClient.Domain;
using DTAClient.Domain.LAN;
using DTAClient.Domain.Multiplayer;
using DTAClient.Domain.Multiplayer.LAN;
using DTAClient.DXGUI.Multiplayer.GameLobby;
using DTAClient.Online;
using Localization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Rampastring.Tools;
using Rampastring.XNAUI;
using Rampastring.XNAUI.XNAControls;
using SixLabors.ImageSharp;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace DTAClient.DXGUI.Multiplayer
{
    internal sealed class LANLobby : XNAWindow
    {
        private const double ALIVE_MESSAGE_INTERVAL = 5.0;
        private const double INACTIVITY_REMOVE_TIME = 10.0;

        public LANLobby(
            WindowManager windowManager,
            GameCollection gameCollection,
            MapLoader mapLoader,
            DiscordHandler discordHandler)
            : base(windowManager)
        {
            this.gameCollection = gameCollection;
            this.mapLoader = mapLoader;
            this.discordHandler = discordHandler;
        }

        public event EventHandler Exited;

        private XNAListBox lbPlayerList;
        private ChatListBox lbChatMessages;
        private GameListBox lbGameList;
        private XNAClientButton btnMainMenu;
        private XNAClientButton btnNewGame;
        private XNAClientButton btnJoinGame;
        private XNAChatTextBox tbChatInput;
        private XNALabel lblColor;
        private XNAClientDropDown ddColor;
        private LANGameCreationWindow gameCreationWindow;
        private LANGameLobby lanGameLobby;
        private LANGameLoadingLobby lanGameLoadingLobby;
        private Texture2D unknownGameIcon;
        private LANColor[] chatColors;
        private string localGame;
        private int localGameIndex;
        private GameCollection gameCollection;
        private Socket socket;
        private IPEndPoint endPoint;
        private Encoding encoding;
        private List<LANLobbyUser> players = new List<LANLobbyUser>();
        private TimeSpan timeSinceAliveMessage = TimeSpan.Zero;
        private MapLoader mapLoader;
        private DiscordHandler discordHandler;
        private bool initSuccess;
        private CancellationTokenSource cancellationTokenSource;
        private IPAddress lanIpV4BroadcastIpAddress;

        public override void Initialize()
        {
            Name = "LANLobby";
            BackgroundTexture = AssetLoader.LoadTexture("cncnetlobbybg.png");
            ClientRectangle = new Rectangle(0, 0, WindowManager.RenderResolutionX - 64,
                WindowManager.RenderResolutionY - 64);

            localGame = ClientConfiguration.Instance.LocalGame;
            localGameIndex = gameCollection.GameList.FindIndex(g => g.InternalName.Equals(localGame, StringComparison.InvariantCultureIgnoreCase));

            btnNewGame = new XNAClientButton(WindowManager);
            btnNewGame.Name = "btnNewGame";
            btnNewGame.ClientRectangle = new Rectangle(12, Height - 35, UIDesignConstants.BUTTON_WIDTH_133, UIDesignConstants.BUTTON_HEIGHT);
            btnNewGame.Text = "Create Game".L10N("UI:Main:CreateGame");
            btnNewGame.LeftClick += (_, _) => BtnNewGame_LeftClickAsync().HandleTask();

            btnJoinGame = new XNAClientButton(WindowManager);
            btnJoinGame.Name = "btnJoinGame";
            btnJoinGame.ClientRectangle = new Rectangle(btnNewGame.Right + 12,
                btnNewGame.Y, UIDesignConstants.BUTTON_WIDTH_133, UIDesignConstants.BUTTON_HEIGHT);
            btnJoinGame.Text = "Join Game".L10N("UI:Main:JoinGame");
            btnJoinGame.LeftClick += (_, _) => JoinGameAsync().HandleTask();

            btnMainMenu = new XNAClientButton(WindowManager);
            btnMainMenu.Name = "btnMainMenu";
            btnMainMenu.ClientRectangle = new Rectangle(Width - 145,
                btnNewGame.Y, UIDesignConstants.BUTTON_WIDTH_133, UIDesignConstants.BUTTON_HEIGHT);
            btnMainMenu.Text = "Main Menu".L10N("UI:Main:MainMenu");
            btnMainMenu.LeftClick += (_, _) => BtnMainMenu_LeftClickAsync().HandleTask();

            lbGameList = new GameListBox(WindowManager, localGame, null);
            lbGameList.Name = "lbGameList";
            lbGameList.ClientRectangle = new Rectangle(btnNewGame.X,
                41, btnJoinGame.Right - btnNewGame.X,
                btnNewGame.Y - 53);
            lbGameList.GameLifetime = 15.0; // Smaller lifetime in LAN
            lbGameList.PanelBackgroundDrawMode = PanelBackgroundImageDrawMode.STRETCHED;
            lbGameList.BackgroundTexture = AssetLoader.CreateTexture(new Color(0, 0, 0, 128), 1, 1);
            lbGameList.DoubleLeftClick += (_, _) => JoinGameAsync().HandleTask();
            lbGameList.AllowMultiLineItems = false;

            lbPlayerList = new XNAListBox(WindowManager);
            lbPlayerList.Name = "lbPlayerList";
            lbPlayerList.ClientRectangle = new Rectangle(Width - 202,
                lbGameList.Y, 190,
                lbGameList.Height);
            lbPlayerList.PanelBackgroundDrawMode = PanelBackgroundImageDrawMode.STRETCHED;
            lbPlayerList.BackgroundTexture = AssetLoader.CreateTexture(new Color(0, 0, 0, 128), 1, 1);
            lbPlayerList.LineHeight = 16;

            lbChatMessages = new ChatListBox(WindowManager);
            lbChatMessages.Name = "lbChatMessages";
            lbChatMessages.ClientRectangle = new Rectangle(lbGameList.Right + 12,
                lbGameList.Y,
                lbPlayerList.X - lbGameList.Right - 24,
                lbGameList.Height);
            lbChatMessages.PanelBackgroundDrawMode = PanelBackgroundImageDrawMode.STRETCHED;
            lbChatMessages.BackgroundTexture = AssetLoader.CreateTexture(new Color(0, 0, 0, 128), 1, 1);
            lbChatMessages.LineHeight = 16;

            tbChatInput = new XNAChatTextBox(WindowManager);
            tbChatInput.Name = "tbChatInput";
            tbChatInput.ClientRectangle = new Rectangle(lbChatMessages.X,
                btnNewGame.Y, lbChatMessages.Width,
                btnNewGame.Height);
            tbChatInput.Suggestion = "Type here to chat...".L10N("UI:Main:ChatHere");
            tbChatInput.MaximumTextLength = 200;
            tbChatInput.EnterPressed += (_, _) => TbChatInput_EnterPressedAsync(cancellationTokenSource?.Token ?? default).HandleTask();

            lblColor = new XNALabel(WindowManager);
            lblColor.Name = "lblColor";
            lblColor.ClientRectangle = new Rectangle(lbChatMessages.X, 14, 0, 0);
            lblColor.FontIndex = 1;
            lblColor.Text = "YOUR COLOR:".L10N("UI:Main:YourColor");

            ddColor = new XNAClientDropDown(WindowManager);
            ddColor.Name = "ddColor";
            ddColor.ClientRectangle = new Rectangle(lblColor.X + 95, 12,
                150, 21);

            chatColors = new LANColor[]
            {
                new LANColor("Gray".L10N("UI:Main:ColorGray"), Color.Gray),
                new LANColor("Metalic".L10N("UI:Main:ColorLightGrayMetalic"), Color.LightGray),
                new LANColor("Green".L10N("UI:Main:ColorGreen"), Color.Green),
                new LANColor("Lime Green".L10N("UI:Main:ColorLimeGreen"), Color.LimeGreen),
                new LANColor("Green Yellow".L10N("UI:Main:ColorGreenYellow"), Color.GreenYellow),
                new LANColor("Goldenrod".L10N("UI:Main:ColorGoldenrod"), Color.Goldenrod),
                new LANColor("Yellow".L10N("UI:Main:ColorYellow"), Color.Yellow),
                new LANColor("Orange".L10N("UI:Main:ColorOrange"), Color.Orange),
                new LANColor("Red".L10N("UI:Main:ColorRed"), Color.Red),
                new LANColor("Pink".L10N("UI:Main:ColorDeepPink"), Color.DeepPink),
                new LANColor("Purple".L10N("UI:Main:ColorMediumPurple"), Color.MediumPurple),
                new LANColor("Sky Blue".L10N("UI:Main:ColorSkyBlue"), Color.SkyBlue),
                new LANColor("Blue".L10N("UI:Main:ColorBlue"), Color.Blue),
                new LANColor("Brown".L10N("UI:Main:ColorSaddleBrown"), Color.SaddleBrown),
                new LANColor("Teal".L10N("UI:Main:ColorTeal"), Color.Teal)
            };

            foreach (LANColor color in chatColors)
            {
                ddColor.AddItem(color.Name, color.XNAColor);
            }

            AddChild(btnNewGame);
            AddChild(btnJoinGame);
            AddChild(btnMainMenu);

            AddChild(lbPlayerList);
            AddChild(lbChatMessages);
            AddChild(lbGameList);
            AddChild(tbChatInput);
            AddChild(lblColor);
            AddChild(ddColor);

            gameCreationWindow = new LANGameCreationWindow(WindowManager);
            var gameCreationPanel = new DarkeningPanel(WindowManager);
            AddChild(gameCreationPanel);
            gameCreationPanel.AddChild(gameCreationWindow);
            gameCreationWindow.Disable();

            gameCreationWindow.NewGame += (_, _) => GameCreationWindow_NewGameAsync().HandleTask();
            gameCreationWindow.LoadGame += (_, e) => GameCreationWindow_LoadGameAsync(e).HandleTask();

            var assembly = Assembly.GetAssembly(typeof(GameCollection));
            using Stream unknownIconStream = assembly.GetManifestResourceStream("ClientCore.Resources.unknownicon.png");

            unknownGameIcon = AssetLoader.TextureFromImage(Image.Load(unknownIconStream));

            encoding = Encoding.UTF8;

            base.Initialize();

            CenterOnParent();
            gameCreationPanel.SetPositionAndSize();

            lanGameLobby = new LANGameLobby(WindowManager, "MultiplayerGameLobby",
                null, chatColors, mapLoader, discordHandler);
            DarkeningPanel.AddAndInitializeWithControl(WindowManager, lanGameLobby);
            lanGameLobby.Disable();

            lanGameLoadingLobby = new LANGameLoadingLobby(WindowManager,
                chatColors, mapLoader, discordHandler);
            DarkeningPanel.AddAndInitializeWithControl(WindowManager, lanGameLoadingLobby);
            lanGameLoadingLobby.Disable();

            int selectedColor = UserINISettings.Instance.LANChatColor;

            ddColor.SelectedIndex = selectedColor >= ddColor.Items.Count || selectedColor < 0
                ? 0 : selectedColor;

            SetChatColor();
            ddColor.SelectedIndexChanged += DdColor_SelectedIndexChanged;

            lanGameLobby.GameLeft += (_, _) => Enable();
            lanGameLobby.GameBroadcast += (_, e) => SendMessageAsync(e.Message, cancellationTokenSource?.Token ?? default).HandleTask();

            lanGameLoadingLobby.GameBroadcast += (_, e) => SendMessageAsync(e.Message, cancellationTokenSource?.Token ?? default).HandleTask();
            lanGameLoadingLobby.GameLeft += (_, _) => Enable();

            WindowManager.GameClosing += (_, _) => WindowManager_GameClosingAsync(cancellationTokenSource?.Token ?? default).HandleTask();
        }

        private async ValueTask WindowManager_GameClosingAsync(CancellationToken cancellationToken)
        {
            if (socket == null)
                return;

            if (socket.IsBound)
                await SendMessageAsync(LANCommands.PLAYER_QUIT_COMMAND, cancellationToken).ConfigureAwait(false);

            cancellationTokenSource.Cancel();
            socket.Close();
        }

        private async ValueTask GameCreationWindow_LoadGameAsync(GameLoadEventArgs e)
        {
            await lanGameLoadingLobby.SetUpAsync(true, null, e.LoadedGameID).ConfigureAwait(false);

            lanGameLoadingLobby.Enable();
        }

        private async ValueTask GameCreationWindow_NewGameAsync()
        {
            await lanGameLobby.SetUpAsync(true,
                new IPEndPoint(IPAddress.Loopback, ProgramConstants.LAN_GAME_LOBBY_PORT), null).ConfigureAwait(false);

            lanGameLobby.Enable();
        }

        private void SetChatColor()
        {
            tbChatInput.TextColor = chatColors[ddColor.SelectedIndex].XNAColor;
            lanGameLobby.SetChatColorIndex(ddColor.SelectedIndex);
            UserINISettings.Instance.LANChatColor.Value = ddColor.SelectedIndex;
        }

        private void DdColor_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetChatColor();
            UserINISettings.Instance.SaveSettings();
        }

        public async ValueTask OpenAsync()
        {
            players.Clear();
            lbPlayerList.Clear();
            lbGameList.ClearGames();
            cancellationTokenSource?.Dispose();

            Visible = true;
            Enabled = true;
            cancellationTokenSource = new CancellationTokenSource();

            Logger.Log("Creating LAN socket.");

            IEnumerable<UnicastIPAddressInformation> lanIpAddresses = NetworkHelper.GetUniCastIpAddresses();
            UnicastIPAddressInformation lanIpV4Address = lanIpAddresses.FirstOrDefault(q => q.Address.AddressFamily is AddressFamily.InterNetwork);

            lanIpV4BroadcastIpAddress = NetworkHelper.GetIpV4BroadcastAddress(lanIpV4Address);

            try
            {
                socket = new Socket(SocketType.Dgram, ProtocolType.Udp)
                {
                    EnableBroadcast = true
                };

                socket.Bind(new IPEndPoint(lanIpV4Address.Address, ProgramConstants.LAN_LOBBY_PORT));

                endPoint = new IPEndPoint(lanIpV4BroadcastIpAddress, ProgramConstants.LAN_LOBBY_PORT);
                initSuccess = true;
            }
            catch (SocketException ex)
            {
                ProgramConstants.LogException(ex, "Creating LAN socket failed!");
                lbChatMessages.AddMessage(new ChatMessage(Color.Red,
                    "Creating LAN socket failed! Message:".L10N("UI:Main:SocketFailure1") + " " + ex.Message));
                lbChatMessages.AddMessage(new ChatMessage(Color.Red,
                    "Please check your firewall settings.".L10N("UI:Main:SocketFailure2")));
                lbChatMessages.AddMessage(new ChatMessage(Color.Red,
                    $"Also make sure that no other application is listening to traffic on UDP ports" +
                    $" {ProgramConstants.LAN_LOBBY_PORT} - {ProgramConstants.LAN_INGAME_PORT}.".L10N("UI:Main:SocketFailure3")));

                initSuccess = false;
                return;
            }

            Logger.Log("Starting listener.");
            ListenAsync(cancellationTokenSource.Token).HandleTask();
            await SendAliveAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        }

        private async ValueTask SendMessageAsync(string message, CancellationToken cancellationToken)
        {
            if (!initSuccess)
                return;

            const int charSize = sizeof(char);
            int bufferSize = message.Length * charSize;
            using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(bufferSize);
            Memory<byte> buffer = memoryOwner.Memory[..bufferSize];
            int bytes = encoding.GetBytes(message.AsSpan(), buffer.Span);

            buffer = buffer[..bytes];

            try
            {
                await socket.SendToAsync(buffer, SocketFlags.None, endPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async ValueTask ListenAsync(CancellationToken cancellationToken)
        {
            try
            {
                using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(4096);

                while (!cancellationToken.IsCancellationRequested)
                {
                    EndPoint ep = new IPEndPoint(lanIpV4BroadcastIpAddress, ProgramConstants.LAN_LOBBY_PORT);
                    Memory<byte> buffer = memoryOwner.Memory[..4096];
                    SocketReceiveFromResult socketReceiveFromResult =
                        await socket.ReceiveFromAsync(buffer, SocketFlags.None, ep, cancellationToken).ConfigureAwait(false);
                    var iep = (IPEndPoint)socketReceiveFromResult.RemoteEndPoint;
                    string data = encoding.GetString(buffer.Span[..socketReceiveFromResult.ReceivedBytes]);

                    if (data == string.Empty)
                        continue;

                    AddCallback(() => HandleNetworkMessage(data, iep));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ProgramConstants.LogException(ex, "LAN socket listener exception.");
            }
        }

        private void HandleNetworkMessage(string data, IPEndPoint endPoint)
        {
            string[] commandAndParams = data.Split(' ');

            if (commandAndParams.Length < 2)
                return;

            string command = commandAndParams[0];

            string[] parameters = data[(command.Length + 1)..].Split(
                new[] { ProgramConstants.LAN_DATA_SEPARATOR },
                StringSplitOptions.RemoveEmptyEntries);

            LANLobbyUser user = players.Find(p => p.EndPoint.Equals(endPoint));

            switch (command)
            {
                case LANCommands.ALIVE:
                    if (parameters.Length < 2)
                        return;

                    int gameIndex = Conversions.IntFromString(parameters[0], -1);
                    string name = parameters[1];

                    if (user == null)
                    {
                        Texture2D gameTexture = unknownGameIcon;

                        if (gameIndex > -1 && gameIndex < gameCollection.GameList.Count)
                            gameTexture = gameCollection.GameList[gameIndex].Texture;

                        user = new LANLobbyUser(name, gameTexture, endPoint);
                        players.Add(user);
                        lbPlayerList.AddItem(user.Name, gameTexture);
                    }

                    user.TimeWithoutRefresh = TimeSpan.Zero;

                    break;
                case LANCommands.CHAT:
                    if (user == null)
                        return;

                    if (parameters.Length < 2)
                        return;

                    int colorIndex = Conversions.IntFromString(parameters[0], -1);

                    if (colorIndex < 0 || colorIndex >= chatColors.Length)
                        return;

                    lbChatMessages.AddMessage(new ChatMessage(user.Name,
                        chatColors[colorIndex].XNAColor, DateTime.Now, parameters[1]));

                    break;
                case LANCommands.QUIT:
                    if (user == null)
                        return;

                    int index = players.FindIndex(p => p == user);

                    players.RemoveAt(index);
                    lbPlayerList.Items.RemoveAt(index);
                    break;
                case LANCommands.GAME:
                    if (user == null)
                        return;

                    HostedLANGame game = new HostedLANGame();
                    if (!game.SetDataFromStringArray(gameCollection, parameters))
                        return;
                    game.EndPoint = endPoint;

                    int existingGameIndex = lbGameList.HostedGames.FindIndex(g => ((HostedLANGame)g).EndPoint.Equals(endPoint));

                    if (existingGameIndex > -1)
                        lbGameList.HostedGames[existingGameIndex] = game;
                    else
                    {
                        lbGameList.HostedGames.Add(game);
                    }

                    lbGameList.Refresh();

                    break;
            }
        }

        private async ValueTask SendAliveAsync(CancellationToken cancellationToken)
        {
            StringBuilder sb = new StringBuilder(LANCommands.ALIVE + " ");
            sb.Append(localGameIndex);
            sb.Append(ProgramConstants.LAN_DATA_SEPARATOR);
            sb.Append(ProgramConstants.PLAYERNAME);
            await SendMessageAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);
            timeSinceAliveMessage = TimeSpan.Zero;
        }

        private async ValueTask TbChatInput_EnterPressedAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(tbChatInput.Text))
                return;

            string chatMessage = tbChatInput.Text.Replace((char)01, '?');

            StringBuilder sb = new StringBuilder(LANCommands.CHAT + " ");
            sb.Append(ddColor.SelectedIndex);
            sb.Append(ProgramConstants.LAN_DATA_SEPARATOR);
            sb.Append(chatMessage);

            await SendMessageAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);

            tbChatInput.Text = string.Empty;
        }

        private async ValueTask JoinGameAsync()
        {
            if (lbGameList.SelectedIndex < 0 || lbGameList.SelectedIndex >= lbGameList.Items.Count)
                return;

            HostedLANGame hg = (HostedLANGame)lbGameList.Items[lbGameList.SelectedIndex].Tag;

            if (!hg.Game.InternalName.Equals(localGame, StringComparison.OrdinalIgnoreCase))
            {
                lbChatMessages.AddMessage(
                    string.Format("The selected game is for {0}!".L10N("UI:Main:GameIsOfPurpose"), gameCollection.GetGameNameFromInternalName(hg.Game.InternalName)));
                return;
            }

            if (hg.Locked)
            {
                lbChatMessages.AddMessage("The selected game is locked!".L10N("UI:Main:GameLocked"));
                return;
            }

            if (hg.IsLoadedGame)
            {
                if (!hg.Players.Contains(ProgramConstants.PLAYERNAME))
                {
                    lbChatMessages.AddMessage("You do not exist in the saved game!".L10N("UI:Main:NotInSavedGame"));
                    return;
                }
            }
            else
            {
                if (hg.Players.Contains(ProgramConstants.PLAYERNAME))
                {
                    lbChatMessages.AddMessage("Your name is already taken in the game.".L10N("UI:Main:NameOccupied"));
                    return;
                }
            }

            if (hg.GameVersion != ProgramConstants.GAME_VERSION)
            {
                // TODO Show warning
            }

            lbChatMessages.AddMessage(string.Format("Attempting to join game {0} ...".L10N("UI:Main:AttemptJoin"), hg.RoomName));

            try
            {
                var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(new IPEndPoint(hg.EndPoint.Address, ProgramConstants.LAN_GAME_LOBBY_PORT), CancellationToken.None).ConfigureAwait(false);

                const int charSize = sizeof(char);

                if (hg.IsLoadedGame)
                {
                    var spawnSGIni = new IniFile(SafePath.CombineFilePath(ProgramConstants.GamePath, ProgramConstants.SAVED_GAME_SPAWN_INI));
                    int loadedGameId = spawnSGIni.GetIntValue("Settings", "GameID", -1);

                    await lanGameLoadingLobby.SetUpAsync(false, client, loadedGameId).ConfigureAwait(false);
                    lanGameLoadingLobby.Enable();

                    string message = LANCommands.PLAYER_JOIN + ProgramConstants.LAN_DATA_SEPARATOR +
                        ProgramConstants.PLAYERNAME + ProgramConstants.LAN_DATA_SEPARATOR +
                        loadedGameId + ProgramConstants.LAN_MESSAGE_SEPARATOR;
                    int bufferSize = message.Length * charSize;
                    using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(bufferSize);
                    Memory<byte> buffer = memoryOwner.Memory[..bufferSize];
                    int bytes = encoding.GetBytes(message.AsSpan(), buffer.Span);
                    buffer = buffer[..bytes];

                    await client.SendAsync(buffer, SocketFlags.None, CancellationToken.None).ConfigureAwait(false);
                    await lanGameLoadingLobby.PostJoinAsync().ConfigureAwait(false);
                }
                else
                {
                    await lanGameLobby.SetUpAsync(false, hg.EndPoint, client).ConfigureAwait(false);
                    lanGameLobby.Enable();

                    string message = LANCommands.PLAYER_JOIN + ProgramConstants.LAN_DATA_SEPARATOR +
                        ProgramConstants.PLAYERNAME + ProgramConstants.LAN_MESSAGE_SEPARATOR;
                    int bufferSize = message.Length * charSize;
                    using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(bufferSize);
                    Memory<byte> buffer = memoryOwner.Memory[..bufferSize];
                    int bytes = encoding.GetBytes(message.AsSpan(), buffer.Span);
                    buffer = buffer[..bytes];

                    await client.SendAsync(buffer, SocketFlags.None, CancellationToken.None).ConfigureAwait(false);
                    await lanGameLobby.PostJoinAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                ProgramConstants.LogException(ex, "Connecting to the game failed!");
                lbChatMessages.AddMessage(null,
                    "Connecting to the game failed! Message:".L10N("UI:Main:ConnectGameFailed") + " " + ex.Message, Color.White);
            }
        }

        private async ValueTask BtnMainMenu_LeftClickAsync()
        {
            Visible = false;
            Enabled = false;
            await SendMessageAsync(LANCommands.PLAYER_QUIT_COMMAND, CancellationToken.None).ConfigureAwait(false);
            cancellationTokenSource.Cancel();
            socket.Close();
            Exited?.Invoke(this, EventArgs.Empty);
        }

        private async ValueTask BtnNewGame_LeftClickAsync()
        {
            if (!ClientConfiguration.Instance.DisableMultiplayerGameLoading)
                gameCreationWindow.Open();
            else
                await GameCreationWindow_NewGameAsync().ConfigureAwait(false);
        }

        public override void Update(GameTime gameTime)
        {
            for (int i = 0; i < players.Count; i++)
            {
                players[i].TimeWithoutRefresh += gameTime.ElapsedGameTime;

                if (players[i].TimeWithoutRefresh > TimeSpan.FromSeconds(INACTIVITY_REMOVE_TIME))
                {
                    lbPlayerList.Items.RemoveAt(i);
                    players.RemoveAt(i);
                    i--;
                }
            }

            timeSinceAliveMessage += gameTime.ElapsedGameTime;
            if (timeSinceAliveMessage > TimeSpan.FromSeconds(ALIVE_MESSAGE_INTERVAL))
                Task.Run(() => SendAliveAsync(cancellationTokenSource?.Token ?? default).HandleTask()).Wait();

            base.Update(gameTime);
        }
    }
}