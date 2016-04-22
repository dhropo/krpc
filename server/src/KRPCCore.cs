using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KRPC.Continuations;
using KRPC.Server;
using KRPC.Server.Stream;
using KRPC.Service;
using KRPC.Service.Messages;
using KRPC.Utils;

namespace KRPC
{
    /// <summary>
    /// The kRPC core. Manages the execution of remote procedures,
    /// bridging the gap between servers and services.
    /// </summary>
    public class KRPCCore
    {
        //TODO: remove servers list, replace with events etc.
        IList<KRPCServer> servers = new List<KRPCServer> ();
        IScheduler<IClient<Request,Response>> clientScheduler;
        IList<RequestContinuation> continuations;
        IDictionary<IClient<byte,StreamMessage>, IList<StreamRequest>> streamRequests;
        IDictionary<uint, object> streamResultCache = new Dictionary<uint, object> ();

        internal delegate double UniversalTimeFunction ();

        internal UniversalTimeFunction GetUniversalTime;

        /// <summary>
        /// Event triggered when a RPC client has connected
        /// </summary>
        public event EventHandler<ClientConnectedArgs> OnRPCClientConnected;

        /// <summary>
        /// Event triggered when a RPC client has disconnected
        /// </summary>
        public event EventHandler<ClientDisconnectedArgs> OnRPCClientDisconnected;

        /// <summary>
        /// Event triggered when a stream client has connected
        /// </summary>
        public event EventHandler<ClientConnectedArgs> OnStreamClientConnected;

        /// <summary>
        /// Event triggered when a stream client has disconnected
        /// </summary>
        public event EventHandler<ClientDisconnectedArgs> OnStreamClientDisconnected;

        internal void RPCClientConnected (IClient<Request,Response> client)
        {
            clientScheduler.Add (client);
            if (OnRPCClientConnected != null)
                OnRPCClientConnected (this, new ClientConnectedArgs (client));
        }

        internal void RPCClientDisconnected (IClient<Request,Response> client)
        {
            clientScheduler.Remove (client);
            if (OnRPCClientDisconnected != null)
                OnRPCClientDisconnected (this, new ClientDisconnectedArgs (client));
        }

        internal void StreamClientConnected (IClient<byte,StreamMessage> client)
        {
            streamRequests [client] = new List<StreamRequest> ();
            if (OnStreamClientConnected != null)
                OnStreamClientConnected (this, new ClientConnectedArgs (client));
        }

        internal void StreamClientDisconnected (IClient<byte,StreamMessage> client)
        {
            streamRequests.Remove (client);
            if (OnStreamClientDisconnected != null)
                OnStreamClientDisconnected (this, new ClientDisconnectedArgs (client));
        }

        /// <summary>
        /// Event triggered when a client performs some activity
        /// </summary>
        public event EventHandler<ClientActivityArgs> OnClientActivity;

        /// <summary>
        /// Stores the context in which a continuation is executed.
        /// For example, used by a continuation to find out which client made the request.
        /// </summary>
        public static class Context
        {
            /// <summary>
            /// The current client
            /// </summary>
            public static IClient RPCClient { get; private set; }

            /// <summary>
            /// The current game scene
            /// </summary>
            public static GameScene GameScene { get; private set; }

            internal static void Set (IClient rpcClient)
            {
                RPCClient = rpcClient;
            }

            internal static void Clear ()
            {
                RPCClient = null;
            }

            internal static void SetGameScene (GameScene gameScene)
            {
                GameScene = gameScene;
            }
        }

        static KRPCCore instance;

        /// <summary>
        /// Instance of KRPCCore
        /// </summary>
        public static KRPCCore Instance {
            get {
                if (instance == null)
                    instance = new KRPCCore ();
                return instance;
            }
        }

        KRPCCore ()
        {
            clientScheduler = new RoundRobinScheduler<IClient<Request,Response>> ();
            continuations = new List<RequestContinuation> ();
            streamRequests = new Dictionary<IClient<byte,StreamMessage>,IList<StreamRequest>> ();

            OneRPCPerUpdate = false;
            MaxTimePerUpdate = 5000;
            AdaptiveRateControl = true;
            BlockingRecv = true;
            RecvTimeout = 1000;
        }

        /// <summary>
        /// Add a server to the core.
        /// </summary>
        public void AddServer (KRPCServer server)
        {
            servers.Add (server);
        }

        /// <summary>
        /// Only execute one RPC for each client per update.
        /// </summary>
        public bool OneRPCPerUpdate { get; set; }

        /// <summary>
        /// Get/set the maximum number of microseconds to spend in a call to FixedUpdate
        /// </summary>
        public uint MaxTimePerUpdate { get; set; }

        /// <summary>
        /// Get/set whether MaxTimePerUpdate should be adjusted to achieve a target framerate.
        /// </summary>
        public bool AdaptiveRateControl { get; set; }

        /// <summary>
        /// Get/set whether FixedUpdate should block for RecvTimeout microseconds to receive RPCs.
        /// </summary>
        public bool BlockingRecv { get; set; }

        /// <summary>
        /// Get/set the timeout for blocking for RPCs, in microseconds.
        /// </summary>
        public uint RecvTimeout { get; set; }

        ExponentialMovingAverage bytesReadRate = new ExponentialMovingAverage (0.25);
        ExponentialMovingAverage bytesWrittenRate = new ExponentialMovingAverage (0.25);

        /// <summary>
        /// Get the total number of bytes read from the network.
        /// </summary>
        public ulong BytesRead {
            get { return servers.Select (x => x.BytesRead).SumUnsignedLong (); }
        }

        /// <summary>
        /// Get the total number of bytes written to the network.
        /// </summary>
        public ulong BytesWritten {
            get { return servers.Select (x => x.BytesWritten).SumUnsignedLong (); }
        }

        /// <summary>
        /// Get the total number of bytes read from the network.
        /// </summary>
        public float BytesReadRate {
            get { return bytesReadRate.Value; }
            set { bytesReadRate.Update (value); }
        }

        /// <summary>
        /// Get the total number of bytes written to the network.
        /// </summary>
        public float BytesWrittenRate {
            get { return bytesWrittenRate.Value; }
            set { bytesWrittenRate.Update (value); }
        }

        ExponentialMovingAverage rpcRate = new ExponentialMovingAverage (0.25);
        ExponentialMovingAverage timePerRPCUpdate = new ExponentialMovingAverage (0.25);
        ExponentialMovingAverage pollTimePerRPCUpdate = new ExponentialMovingAverage (0.25);
        ExponentialMovingAverage execTimePerRPCUpdate = new ExponentialMovingAverage (0.25);
        ExponentialMovingAverage streamRPCRate = new ExponentialMovingAverage (0.25);
        ExponentialMovingAverage timePerStreamUpdate = new ExponentialMovingAverage (0.25);

        /// <summary>
        /// Total number of RPCs executed.
        /// </summary>
        public ulong RPCsExecuted { get; private set; }

        /// <summary>
        /// Number of RPCs processed per second.
        /// </summary>
        public float RPCRate {
            get { return rpcRate.Value; }
            set { rpcRate.Update (value); }
        }

        /// <summary>
        /// Time taken by the update loop per update, in seconds.
        /// </summary>
        public float TimePerRPCUpdate {
            get { return timePerRPCUpdate.Value; }
            set { timePerRPCUpdate.Update (value); }
        }

        /// <summary>
        /// Time taken polling for new RPCs per update, in seconds.
        /// </summary>
        public float PollTimePerRPCUpdate {
            get { return pollTimePerRPCUpdate.Value; }
            set { pollTimePerRPCUpdate.Update (value); }
        }

        /// <summary>
        /// Time taken polling executing RPCs per update, in seconds.
        /// </summary>
        public float ExecTimePerRPCUpdate {
            get { return execTimePerRPCUpdate.Value; }
            set { execTimePerRPCUpdate.Update (value); }
        }

        /// <summary>
        /// Number of currently active streaming RPCs.
        /// </summary>
        public uint StreamRPCs { get; private set; }

        /// <summary>
        /// Total number of streaming RPCs executed.
        /// </summary>
        public ulong StreamRPCsExecuted { get; private set; }

        /// <summary>
        /// Number of streaming RPCs processed per second.
        /// </summary>
        public float StreamRPCRate {
            get { return streamRPCRate.Value; }
            set { streamRPCRate.Update (value); }
        }

        /// <summary>
        /// Time taken by the stream update loop, in seconds.
        /// </summary>
        public float TimePerStreamUpdate {
            get { return timePerStreamUpdate.Value; }
            set { timePerStreamUpdate.Update (value); }
        }

        /// <summary>
        /// Clear the server statistics.
        /// </summary>
        public void ClearStats ()
        {
            RPCsExecuted = 0;
            RPCRate = 0;
            TimePerRPCUpdate = 0;
            ExecTimePerRPCUpdate = 0;
            PollTimePerRPCUpdate = 0;
            StreamRPCs = 0;
            StreamRPCsExecuted = 0;
            TimePerStreamUpdate = 0;
        }

        Stopwatch updateTimer = Stopwatch.StartNew ();

        /// <summary>
        /// Update the server
        /// </summary>
        public void Update ()
        {
            ulong startRPCsExecuted = RPCsExecuted;
            ulong startStreamRPCsExecuted = StreamRPCsExecuted;
            ulong startBytesRead = BytesRead;
            ulong startBytesWritten = BytesWritten;

            RPCServerUpdate ();
            StreamServerUpdate ();

            var timeElapsed = updateTimer.ElapsedSeconds ();
            var ticksElapsed = updateTimer.ElapsedTicks;
            updateTimer.Reset ();
            updateTimer.Start ();

            RPCRate = (float)((double)(RPCsExecuted - startRPCsExecuted) / timeElapsed);
            StreamRPCRate = (float)((double)(StreamRPCsExecuted - startStreamRPCsExecuted) / timeElapsed);
            BytesReadRate = (float)((double)(BytesRead - startBytesRead) / timeElapsed);
            BytesWrittenRate = (float)((double)(BytesWritten - startBytesWritten) / timeElapsed);

            // Adjust MaxTimePerUpdate to get a target FixedUpdate rate of 59 FPS. This is slightly smaller
            // than 60 FPS, so that it pushes against the target 60 FPS for FixedUpdate.
            // The minimum MaxTimePerUpdate that will be set is 1ms, and the maximum is 25ms.
            // If very little time is being spent executing RPCs (<1ms), MaxTimePerUpdate is set to 10ms.
            // This prevents MaxTimePerUpdate from being set to a high value when the server is idle, which would
            // cause a drop in framerate if a large burst of RPCs are received.
            if (AdaptiveRateControl) {
                var targetTicks = Stopwatch.Frequency / 59;
                if (ticksElapsed > targetTicks) {
                    if (MaxTimePerUpdate > 1000)
                        MaxTimePerUpdate -= 100;
                } else {
                    if (ExecTimePerRPCUpdate < 0.001) {
                        MaxTimePerUpdate = 10000;
                    } else {
                        if (MaxTimePerUpdate < 25000)
                            MaxTimePerUpdate += 100;
                    }
                }
            }
        }

        /// <summary>
        /// Update the RPC server, called once every FixedUpdate.
        /// This method receives and executes RPCs, for up to MaxTimePerUpdate microseconds.
        /// RPCs are delayed to the next update if this time expires. If AdaptiveRateControl
        /// is true, MaxTimePerUpdate will be automatically adjusted to achieve a target framerate.
        /// If NonBlockingUpdate is false, this call will block waiting for new RPCs for up to
        /// MaxPollTimePerUpdate microseconds. If NonBlockingUpdate is true, a single non-blocking call
        /// will be made to check for new RPCs.
        /// </summary>
        void RPCServerUpdate ()
        {
            var timer = Stopwatch.StartNew ();
            var pollTimeout = new Stopwatch ();
            var pollTimer = new Stopwatch ();
            var execTimer = new Stopwatch ();
            long maxTimePerUpdateTicks = StopwatchExtensions.MicrosecondsToTicks (MaxTimePerUpdate);
            long recvTimeoutTicks = StopwatchExtensions.MicrosecondsToTicks (RecvTimeout);
            ulong rpcsExecuted = 0;

            var yieldedContinuations = new List<RequestContinuation> ();
            foreach (var server in servers)
                server.RPCServer.Update ();

            while (true) {

                // Poll for RPCs
                pollTimer.Start ();
                pollTimeout.Reset ();
                pollTimeout.Start ();
                while (true) {
                    PollRequests (yieldedContinuations);
                    if (!BlockingRecv)
                        break;
                    if (pollTimeout.ElapsedTicks > recvTimeoutTicks)
                        break;
                    if (timer.ElapsedTicks > maxTimePerUpdateTicks)
                        break;
                    if (continuations.Any ())
                        break;
                }
                pollTimer.Stop ();

                if (!continuations.Any ())
                    break;

                // Execute RPCs
                execTimer.Start ();
                foreach (var continuation in continuations) {

                    // Ignore the continuation if the client has disconnected
                    if (!continuation.Client.Connected)
                        continue;

                    // Max exec time exceeded, delay to next update
                    if (timer.ElapsedTicks > maxTimePerUpdateTicks) {
                        yieldedContinuations.Add (continuation);
                        continue;
                    }

                    // Execute the continuation
                    try {
                        ExecuteContinuation (continuation);
                    } catch (YieldException e) {
                        yieldedContinuations.Add ((RequestContinuation)e.Continuation);
                    }
                    rpcsExecuted++;
                }
                continuations.Clear ();
                execTimer.Stop ();

                // Exit if only execute one RPC per update
                if (OneRPCPerUpdate)
                    break;

                // Exit if max exec time exceeded
                if (timer.ElapsedTicks > maxTimePerUpdateTicks)
                    break;
            }

            // Run yielded continuations on the next update
            continuations = yieldedContinuations;

            timer.Stop ();

            RPCsExecuted += rpcsExecuted;
            TimePerRPCUpdate = (float)timer.ElapsedSeconds ();
            PollTimePerRPCUpdate = (float)pollTimer.ElapsedSeconds ();
            ExecTimePerRPCUpdate = (float)execTimer.ElapsedSeconds ();
        }

        /// <summary>
        /// Update the Stream server. Executes all streaming RPCs and sends the results to clients (if they have changed).
        /// </summary>
        void StreamServerUpdate ()
        {
            Stopwatch timer = Stopwatch.StartNew ();
            uint rpcsExecuted = 0;

            foreach (var server in servers)
                server.StreamServer.Update ();

            // Run streaming requests
            foreach (var entry in streamRequests) {
                var streamClient = entry.Key;
                var requests = entry.Value;
                if (!requests.Any ())
                    continue;
                var streamMessage = new StreamMessage ();
                foreach (var request in requests) {
                    // Run the RPC
                    Response response;
                    try {
                        response = KRPC.Service.Services.Instance.HandleRequest (request.Procedure, request.Arguments);
                    } catch (Exception e) {
                        response = new Response ();
                        response.HasError = true;
                        response.Error = e.ToString ();
                    }
                    rpcsExecuted++;
                    // Don't send an update if it is the previous one
                    //FIXME: does the following comparison work?!? The objects have not been serialized
                    if (response.ReturnValue == streamResultCache [request.Identifier])
                        continue;
                    // Add the update to the response message
                    streamResultCache [request.Identifier] = response.ReturnValue;
                    response.Time = GetUniversalTime ();
                    var streamResponse = request.Response;
                    streamResponse.Response = response;
                    streamMessage.Responses.Add (streamResponse);
                }
                //FIXME: this cast won't work for generic clients
                ((IClient<byte,StreamMessage>)streamClient).Stream.Write (streamMessage);
            }

            timer.Stop ();
            StreamRPCs = rpcsExecuted;
            StreamRPCsExecuted += rpcsExecuted;
            TimePerStreamUpdate = (float)timer.ElapsedSeconds ();
        }

        IClient<byte,StreamMessage> GetStreamClient (IClient rpcClient)
        {
            // Find stream client corresponding to the RPC client
            IClient<byte,StreamMessage> streamClient = null;
            foreach (var server in servers) {
                streamClient = server.StreamServer.Clients.SingleOrDefault (c => c.Guid == rpcClient.Guid);
                if (streamClient != null)
                    return streamClient;
            }
            throw new ArgumentException ("Stream client does not exist");
        }

        /// <summary>
        /// Add a stream to the server
        /// </summary>
        internal uint AddStream (IClient rpcClient, Service.Messages.Request request)
        {   
            var streamClient = GetStreamClient (rpcClient);

            // Check for an existing stream for the request
            var procedure = KRPC.Service.Services.Instance.GetProcedureSignature (request.Service, request.Procedure);
            var arguments = KRPC.Service.Services.Instance.GetArguments (procedure, request.Arguments);
            foreach (var streamRequest in streamRequests[streamClient]) {
                if (streamRequest.Procedure == procedure && streamRequest.Arguments.SequenceEqual (arguments))
                    return streamRequest.Identifier;
            }

            // Create a new stream
            {
                var streamRequest = new StreamRequest (request);
                streamRequests [streamClient].Add (streamRequest);
                streamResultCache [streamRequest.Identifier] = null;
                return streamRequest.Identifier;
            }
        }

        /// <summary>
        /// Remove a stream from the server
        /// </summary>
        internal void RemoveStream (IClient rpcClient, uint identifier)
        {
            var streamClient = GetStreamClient (rpcClient);
            var requests = streamRequests [streamClient].Where (x => x.Identifier == identifier).ToList ();
            if (!requests.Any ())
                return;
            streamRequests [streamClient].Remove (requests.Single ());
            streamResultCache.Remove (identifier);
        }

        /// <summary>
        /// Poll connected clients for new requests.
        /// Adds a continuation to the queue for any client with a new request,
        /// if a continuation is not already being processed for the client.
        /// </summary>
        void PollRequests (IEnumerable<RequestContinuation> yieldedContinuations)
        {
            var currentClients = continuations.Select (((c) => c.Client)).ToList ();
            currentClients.AddRange (yieldedContinuations.Select (((c) => c.Client)));
            foreach (var client in clientScheduler) {
                if (!currentClients.Contains (client) && client.Stream.DataAvailable) {
                    Request request = client.Stream.Read ();
                    if (OnClientActivity != null)
                        OnClientActivity (this, new ClientActivityArgs (client));
                    if (Logger.ShouldLog (Logger.Severity.Debug))
                        Logger.WriteLine ("Received request from client " + client.Address + " (" + request.Service + "." + request.Procedure + ")", Logger.Severity.Debug);
                    continuations.Add (new RequestContinuation (client, request));
                }
            }
        }

        /// <summary>
        /// Execute the continuation and send a response to the client,
        /// or throw a YieldException if the continuation is not complete.
        /// </summary>
        void ExecuteContinuation (RequestContinuation continuation)
        {
            var client = continuation.Client;

            // Run the continuation, and either return a result, an error,
            // or throw a YieldException if the continuation has not completed
            Response response;
            try {
                Context.Set (client);
                response = continuation.Run ();
            } catch (YieldException) {
                throw;
            } catch (RPCException e) {
                response = new Response ();
                response.HasError = true;
                response.Error = e.Message;
                if (Logger.ShouldLog (Logger.Severity.Debug))
                    Logger.WriteLine (e.Message, Logger.Severity.Debug);
            } catch (Exception e) {
                response = new Response ();
                response.HasError = true;
                response.Error = e.Message + "\n" + e.StackTrace;
                if (Logger.ShouldLog (Logger.Severity.Debug))
                    Logger.WriteLine (e.Message + "\n" + e.StackTrace, Logger.Severity.Debug);
            } finally {
                Context.Clear ();
            }

            // Send response to the client
            response.Time = GetUniversalTime ();
            client.Stream.Write (response);
            if (Logger.ShouldLog (Logger.Severity.Debug)) {
                if (response.HasError)
                    Logger.WriteLine ("Sent error response to client " + client.Address + " (" + response.Error + ")", Logger.Severity.Debug);
                else
                    Logger.WriteLine ("Sent response to client " + client.Address, Logger.Severity.Debug);
            }
        }
    }
}
