﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Tools;
using Google.ProtocolBuffers;

namespace Akka.Remote.Transport
{
    internal class ProtocolTransportAddressPair
    {
        public ProtocolTransportAddressPair(AkkaProtocolTransport protocolTransport, Address address)
        {
            ProtocolTransport = protocolTransport;
            Address = address;
        }

        public AkkaProtocolTransport ProtocolTransport { get; private set; }

        public Address Address { get; private set; }
    }

    public class AkkaProtocolException : AkkaException
    {
        public AkkaProtocolException(string message, Exception cause = null) : base(message, cause) { }
    }

    /// <summary>
    /// Implementation of the Akka protocol as a (logical) <see cref="Transport"/> that wraps an underlying (physical) <see cref="Transport"/> instance.
    /// 
    /// Features provided by this transport include:
    ///  - Soft-state associations via the use of heartbeats and failure detectors
    ///  - Transparent origin address handling
    /// 
    /// This transport is loaded automatically by <see cref="Remoting"/> and will wrap all dynamically loaded transports.
    /// </summary>
    internal class AkkaProtocolTransport : ActorTransportAdapter
    {
        public AkkaProtocolTransport(Transport wrappedTransport, ActorSystem system, AkkaProtocolSettings settings, AkkaPduCodec codec)
            : base(wrappedTransport, system)
        {
            Codec = codec;
            Settings = settings;
        }

        public AkkaProtocolSettings Settings { get; private set; }

        protected AkkaPduCodec Codec { get; private set; }

        private readonly SchemeAugmenter _schemeAugmenter = new SchemeAugmenter(RemoteSettings.AkkaScheme);

        protected override SchemeAugmenter SchemeAugmenter
        {
            get { return _schemeAugmenter; }
        }

        private string _managerName;
        protected override string ManagerName
        {
            get
            {
                if (string.IsNullOrEmpty(_managerName))
                    _managerName = string.Format("akkaprotocolmanager.{0}.{1}", WrappedTransport.SchemeIdentifier,
                        UniqueId.GetAndIncrement());
                return _managerName;
            }
        }

        private Props _managerProps;
        protected override Props ManagerProps
        {
            get {
                return _managerProps ??
                       (_managerProps =
                           Props.Create(() => new AkkaProtocolManager(WrappedTransport, Settings))
                               .WithDeploy(Deploy.Local));
            }
        }

        public override Task<bool> ManagementCommand(object message)
        {
            return WrappedTransport.ManagementCommand(message);
        }

        public Task<AkkaProtocolHandle> Associate(Address remoteAddress, int? refuseUid)
        {
            // Prepare a Task and pass its completion source to the manager
            var statusPromise = new TaskCompletionSource<AssociationHandle>();

            manager.Tell(new AssociateUnderlyingRefuseUid(SchemeAugmenter.RemoveScheme(remoteAddress), statusPromise, refuseUid));

            return statusPromise.Task.ContinueWith(result => result.Result.AsInstanceOf<AkkaProtocolHandle>(),
                TaskContinuationOptions.AttachedToParent | TaskContinuationOptions.ExecuteSynchronously);
        }

        #region Static properties

        public static AtomicCounter UniqueId = new AtomicCounter(0);

        #endregion
    }

    internal class AkkaProtocolManager : ActorTransportAdapterManager
    {
        public AkkaProtocolManager(Transport wrappedTransport, AkkaProtocolSettings settings)
        {
            _wrappedTransport = wrappedTransport;
            _settings = settings;
        }

        private Transport _wrappedTransport;

        private AkkaProtocolSettings _settings;

        /// <summary>
        /// The <see cref="AkkaProtocolTransport"/> does not handle recovery of associations, this task is implemented
        /// in the remoting itself. Hence the strategy <see cref="Directive.Stop"/>.
        /// </summary>
        private readonly SupervisorStrategy _supervisor = new OneForOneStrategy(exception => Directive.Stop);
        protected override SupervisorStrategy SupervisorStrategy()
        {
            return _supervisor;
        }

        #region ActorBase / ActorTransportAdapterManager overrides

        protected override void Ready(object message)
        {
            message.Match()
                .With<InboundAssociation>(ia => //need to create an Inbound ProtocolStateActor
                {
                    var handle = ia.Association;
                    var stateActorLocalAddress = localAddress;
                    var stateActorAssociationListener = associationListener;
                    var stateActorSettings = _settings;
                    var failureDetector = CreateTransportFailureDetector();
                    //TODO: eventually this needs to be configured with the RemoteDispatcher via https://github.com/akka/akka/blob/f1edf789798dc02dfa37d3301d7712736c964ab1/akka-remote/src/main/scala/akka/remote/transport/AkkaProtocolTransport.scala#L156
                    Context.ActorOf(ProtocolStateActor.InboundProps(
                        new HandshakeInfo(stateActorLocalAddress, AddressUidExtension.Uid(Context.System)), 
                        handle,
                        stateActorAssociationListener,
                        stateActorSettings,
                        new AkkaPduProtobuffCodec(),
                        failureDetector), ActorNameFor(handle.RemoteAddress));
                })
                .With<AssociateUnderlying>(au => CreateOutboundStateActor(au.RemoteAddress, au.StatusPromise, null)) //need to create an Outbond ProtocolStateActor
                .With<AssociateUnderlyingRefuseUid>(au => CreateOutboundStateActor(au.RemoteAddress, au.StatusCompletionSource, au.RefuseUid));
        }

        #endregion

        #region Actor creation methods

        private string ActorNameFor(Address remoteAddress)
        {
            return string.Format("akkaProtocol-{0}-{1}", AddressUrlEncoder.Encode(remoteAddress), nextId());
        }

        private void CreateOutboundStateActor(Address remoteAddress,
            TaskCompletionSource<AssociationHandle> statusPromise, int? refuseUid)
        {
            var stateActorLocalAddress = localAddress;
            var stateActorSettings = _settings;
            var stateActorWrappedTransport = _wrappedTransport;
            var failureDetector = CreateTransportFailureDetector();

            //TODO: eventually this needs to be configured with the RemoteDispatcher via https://github.com/akka/akka/blob/f1edf789798dc02dfa37d3301d7712736c964ab1/akka-remote/src/main/scala/akka/remote/transport/AkkaProtocolTransport.scala#L156
            Context.ActorOf(ProtocolStateActor.OutboundProps(
                new HandshakeInfo(stateActorLocalAddress, AddressUidExtension.Uid(Context.System)),
                remoteAddress,
                statusPromise,
                stateActorWrappedTransport,
                stateActorSettings,
                new AkkaPduProtobuffCodec(), failureDetector, refuseUid),
                ActorNameFor(remoteAddress));
        }

        private FailureDetector CreateTransportFailureDetector()
        {
            return FailureDetectorLoader.LoadFailureDetector(Context, _settings.TransportFailureDetectorImplementationClass,
                _settings.TransportFailureDetectorConfig);
        }

        #endregion
    }

    internal class AssociateUnderlyingRefuseUid : NoSerializationVerificationNeeded
    {
        public AssociateUnderlyingRefuseUid(Address remoteAddress, TaskCompletionSource<AssociationHandle> statusCompletionSource, int? refuseUid = null)
        {
            RefuseUid = refuseUid;
            StatusCompletionSource = statusCompletionSource;
            RemoteAddress = remoteAddress;
        }

        public Address RemoteAddress { get; private set; }

        public TaskCompletionSource<AssociationHandle> StatusCompletionSource { get; private set; }

        public int? RefuseUid { get; private set; }
    }

    internal sealed class HandshakeInfo
    {
        public HandshakeInfo(Address origin, long uid)
        {
            Origin = origin;
            Uid = uid;
        }

        public Address Origin { get; private set; }

        public long Uid { get; private set; }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is HandshakeInfo && Equals((HandshakeInfo) obj);
        }

        private bool Equals(HandshakeInfo other)
        {
            return Equals(Origin, other.Origin) && Uid == other.Uid;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Origin != null ? Origin.GetHashCode() : 0) * 397) ^ Uid.GetHashCode();
            }
        }
    }

    internal class AkkaProtocolHandle : AbstractTransportAdapterHandle
    {
        public AkkaProtocolHandle(Address originalLocalAddress, Address originalRemoteAddress,
            TaskCompletionSource<IHandleEventListener> readHandlerCompletionSource, AssociationHandle wrappedHandle,
            HandshakeInfo handshakeInfo, ActorRef stateActor, AkkaPduCodec codec)
            : base(originalLocalAddress, originalRemoteAddress, wrappedHandle, RemoteSettings.AkkaScheme)
        {
            HandshakeInfo = handshakeInfo;
            StateActor = stateActor;
            ReadHandlerSource = readHandlerCompletionSource;
            Codec = codec;
        }

        public readonly HandshakeInfo HandshakeInfo;

        public readonly ActorRef StateActor;

        public readonly AkkaPduCodec Codec;

        public override bool Write(ByteString payload)
        {
            return WrappedHandle.Write(Codec.ConstructPayload(payload));
        }

        public override void Disassociate()
        {
            Disassociate(DisassociateInfo.Unknown);
        }

        public void Disassociate(DisassociateInfo info)
        {
            StateActor.Tell(new DisassociateUnderlying(info));
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((AkkaProtocolHandle) obj);
        }

        protected bool Equals(AkkaProtocolHandle other)
        {
            return base.Equals(other) && Equals(HandshakeInfo, other.HandshakeInfo) && Equals(StateActor, other.StateActor);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = base.GetHashCode();
                hashCode = (hashCode * 397) ^ (HandshakeInfo != null ? HandshakeInfo.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (StateActor != null ? StateActor.GetHashCode() : 0);
                return hashCode;
            }
        }
    }

    internal enum AssociationState
    {
        Closed = 0,
        WaitHandshake = 1,
        Open = 2
    }

    internal class HeartbeatTimer : NoSerializationVerificationNeeded { }

    internal sealed class HandleMsg : NoSerializationVerificationNeeded
    {
        public HandleMsg(AssociationHandle handle)
        {
            Handle = handle;
        }

        public AssociationHandle Handle { get; private set; }
    }

    internal sealed class HandleListenerRegistered : NoSerializationVerificationNeeded
    {
        public HandleListenerRegistered(IHandleEventListener listener)
        {
            Listener = listener;
        }

        public IHandleEventListener Listener { get; private set; }
    }

    internal abstract class ProtocolStateData { }
    internal abstract class InitialProtocolStateData : ProtocolStateData { }

    /// <summary>
    /// Neither the underlying nor the provided transport is associated
    /// </summary>
    internal sealed class OutboundUnassociated : InitialProtocolStateData
    {
        public OutboundUnassociated(Address remoteAddress, TaskCompletionSource<AssociationHandle> statusCompletionSource, Transport transport)
        {
            Transport = transport;
            StatusCompletionSource = statusCompletionSource;
            RemoteAddress = remoteAddress;
        }

        public Address RemoteAddress { get; private set; }

        public TaskCompletionSource<AssociationHandle> StatusCompletionSource { get; private set; }

        public Transport Transport { get; private set; }
    }

    /// <summary>
    /// The underlying transport is associated, but the handshake of the Akka protocol is not yet finished
    /// </summary>
    internal sealed class OutboundUnderlyingAssociated : ProtocolStateData
    {
        public OutboundUnderlyingAssociated(TaskCompletionSource<AssociationHandle> statusCompletionSource, AssociationHandle wrappedHandle)
        {
            WrappedHandle = wrappedHandle;
            StatusCompletionSource = statusCompletionSource;
        }

        public TaskCompletionSource<AssociationHandle> StatusCompletionSource { get; private set; }

        public AssociationHandle WrappedHandle { get; private set; }
    }

    /// <summary>
    /// The underlying transport is associated, but the handshake of the akka protocol is not yet finished
    /// </summary>
    internal sealed class InboundUnassociated : InitialProtocolStateData
    {
        public InboundUnassociated(IAssociationEventListener associationEventListener, AssociationHandle wrappedHandle)
        {
            WrappedHandle = wrappedHandle;
            AssociationEventListener = associationEventListener;
        }

        public IAssociationEventListener AssociationEventListener { get; private set; }

        public AssociationHandle WrappedHandle { get; private set; }
    }

    /// <summary>
    /// The underlying transport is associated, but the handler for the handle has not been provided yet
    /// </summary>
    internal sealed class AssociatedWaitHandler : ProtocolStateData
    {
        public AssociatedWaitHandler(Task<IHandleEventListener> handlerListener, AssociationHandle wrappedHandle, Queue<ByteString> queue)
        {
            Queue = queue;
            WrappedHandle = wrappedHandle;
            HandlerListener = handlerListener;
        }

        public Task<IHandleEventListener> HandlerListener { get; private set; }

        public AssociationHandle WrappedHandle { get; private set; }

        public Queue<ByteString> Queue { get; private set; }
    }

    /// <summary>
    /// System ready!
    /// </summary>
    internal sealed class ListenerReady : ProtocolStateData
    {
        public ListenerReady(IHandleEventListener listener, AssociationHandle wrappedHandle)
        {
            WrappedHandle = wrappedHandle;
            Listener = listener;
        }

        public IHandleEventListener Listener { get; private set; }

        public AssociationHandle WrappedHandle { get; private set; }
    }

    internal class TimeoutReason { }
    internal class ForbiddenUidReason { }

    internal class ProtocolStateActor : FSM<AssociationState, ProtocolStateData>
    {
        private InitialProtocolStateData _initialData;
        private HandshakeInfo _localHandshakeInfo;
        private int? _refuseUid;
        private AkkaProtocolSettings _settings;
        private Address _localAddress;
        private AkkaPduCodec _codec;
        private FailureDetector _failureDetector;

        /// <summary>
        /// Constructor for outbound ProtocolStateActors
        /// </summary>
        public ProtocolStateActor(HandshakeInfo handshakeInfo, Address remoteAddress,
            TaskCompletionSource<AssociationHandle> statusCompletionSource, Transport transport,
            AkkaProtocolSettings settings, AkkaPduCodec codec, FailureDetector failureDetector, int? refuseUid = null)
            : this(
                new OutboundUnassociated(remoteAddress, statusCompletionSource, transport), handshakeInfo, settings, codec, failureDetector,
                refuseUid)
        {

        }

        /// <summary>
        /// Constructor for inbound ProtocolStateActors
        /// </summary>
        public ProtocolStateActor(HandshakeInfo handshakeInfo, AssociationHandle wrappedHandle, IAssociationEventListener associationEventListener, AkkaProtocolSettings settings, AkkaPduCodec codec, FailureDetector failureDetector)
            : this(new InboundUnassociated(associationEventListener, wrappedHandle), handshakeInfo, settings, codec, failureDetector, refuseUid: null) { }

        /// <summary>
        /// Common constructor used by both the outbound and the inboud cases
        /// </summary>
        protected ProtocolStateActor(InitialProtocolStateData initialData, HandshakeInfo localHandshakeInfo, AkkaProtocolSettings settings, AkkaPduCodec codec, FailureDetector failureDetector, int? refuseUid)
        {
            _initialData = initialData;
            _localHandshakeInfo = localHandshakeInfo;
            _settings = settings;
            _refuseUid = refuseUid;
            _localAddress = _localHandshakeInfo.Origin;
            _codec = codec;
            _failureDetector = failureDetector;
            InitializeFSM();
        }

        #region FSM bindings

        private void InitializeFSM()
        {
            When(AssociationState.Closed, fsmEvent =>
            {
                State<AssociationState, ProtocolStateData> nextState = null;
                //Transport layer events for outbound associations
                fsmEvent.FsmEvent.Match()
                    .With<Status.Failure>(f => fsmEvent.StateData.Match()
                        .With<OutboundUnassociated>(ou =>
                        {
                            ou.StatusCompletionSource.SetException(f.Cause);
                            nextState = Stop();
                        }))
                    .With<AssociationHandle>(h => fsmEvent.StateData.Match()
                        .With<OutboundUnassociated>(ou =>
                        {
                            var wrappedHandle = h;
                            var statusPromise = ou.StatusCompletionSource;
                            wrappedHandle.ReadHandlerSource.SetResult(new ActorHandleEventListener(Self));
                            if (SendAssociate(wrappedHandle, _localHandshakeInfo))
                            {
                                _failureDetector.HeartBeat();
                                InitTimers();
                                nextState =
                                    GoTo(AssociationState.WaitHandshake)
                                        .Using(new OutboundUnderlyingAssociated(statusPromise, wrappedHandle));
                            }
                            else
                            {
                                SetTimer("associate-retry", wrappedHandle,
                                    Context.System.Provider.AsInstanceOf<RemoteActorRefProvider>()
                                        .RemoteSettings.BackoffPeriod, repeat: false);
                                nextState = Stay();
                            }
                        }))
                    .With<DisassociateUnderlying>(d =>
                    {
                        nextState = Stop();
                    })
                    .Default(m => { nextState = Stay(); });

                return nextState;
            });

            //Transport layer events for outbound associations
            When(AssociationState.WaitHandshake, @event =>
            {
                State<AssociationState, ProtocolStateData> nextState = null;

                @event.FsmEvent.Match()
                    .With<Disassociated>(d =>
                    {
                        nextState = Stop(new Failure(d.Info));
                    })
                    .With<InboundPayload>(m =>
                    {
                        var pdu = DecodePdu(m.Payload);
                        @event.StateData.Match()
                            .With<OutboundUnderlyingAssociated>(ola =>
                            {
                                var wrappedHandle = ola.WrappedHandle;
                                var statusCompletionSource = ola.StatusCompletionSource;
                                pdu.Match()
                                    .With<Associate>(a =>
                                    {

                                        var handshakeInfo = a.Info;
                                        if (_refuseUid.HasValue && _refuseUid == handshakeInfo.Uid) //refused UID
                                        {
                                            SendDisassociate(wrappedHandle, DisassociateInfo.Quarantined);
                                            nextState = Stop(new Failure(new ForbiddenUidReason()));
                                        }
                                        else //accepted UID
                                        {
                                            _failureDetector.HeartBeat();
                                            nextState =
                                                GoTo(AssociationState.Open)
                                                    .Using(
                                                        new AssociatedWaitHandler(
                                                            NotifyOutboundHandler(wrappedHandle, handshakeInfo,
                                                                statusCompletionSource), wrappedHandle,
                                                            new Queue<ByteString>()));
                                        }
                                    })
                                    .With<Disassociate>(d =>
                                    {
                                        //After receiving Disassociate we MUST NOT send back a Disassociate (loop)
                                        nextState = Stop(new Failure(d.Reason));
                                    })
                                    .Default(d =>
                                    {
                                        _log.Debug(string.Format("Exepcted message of type Associate; instead received {0}", d));
                                        //Expect handshake to be finished, dropping connection
                                        SendDisassociate(wrappedHandle, DisassociateInfo.Unknown);
                                        nextState = Stop();
                                    });
                            })
                            .With<InboundUnassociated>(iu =>
                            {
                                var associationHandler = iu.AssociationEventListener;
                                var wrappedHandle = iu.WrappedHandle;
                                pdu.Match()
                                    .With<Disassociate>(d => nextState = Stop(new Failure(d.Reason)))
                                    .With<Associate>(a =>
                                    {
                                        SendAssociate(wrappedHandle, _localHandshakeInfo);
                                        _failureDetector.HeartBeat();
                                        InitTimers();
                                        nextState =
                                            GoTo(AssociationState.Open)
                                                .Using(
                                                    new AssociatedWaitHandler(
                                                        NotifyInboundHandler(wrappedHandle, a.Info, associationHandler),
                                                        wrappedHandle, new Queue<ByteString>()));
                                    })
                                    .Default(d =>
                                    {
                                        SendDisassociate(wrappedHandle, DisassociateInfo.Unknown);
                                        nextState = Stop();
                                    });
                            });

                    })
                    .With<HeartbeatTimer>(h => @event.StateData.Match()
                        .With<OutboundUnderlyingAssociated>(ou => nextState = HandleTimers(ou.WrappedHandle)));

                return nextState;
            });

            When(AssociationState.Open, @event =>
            {
                State<AssociationState, ProtocolStateData> nextState = null;
                @event.FsmEvent.Match()
                    .With<Disassociated>(d => nextState = Stop(new Failure(d.Info)))
                    .With<InboundPayload>(ip =>
                    {
                        var pdu = DecodePdu(ip.Payload);
                        pdu.Match()
                            .With<Disassociate>(d => nextState = Stop(new Failure(d.Reason)))
                            .With<Heartbeat>(h =>
                            {
                                _failureDetector.HeartBeat();
                                nextState = Stay();
                            })
                            .With<Payload>(p => @event.StateData.Match()
                                .With<AssociatedWaitHandler>(awh =>
                                {
                                    var nQueue = new Queue<ByteString>(awh.Queue);
                                    nQueue.Enqueue(p.Bytes);
                                    nextState =
                                        Stay()
                                            .Using(new AssociatedWaitHandler(awh.HandlerListener, awh.WrappedHandle,
                                                nQueue));
                                })
                                .With<ListenerReady>(lr =>
                                {
                                    lr.Listener.Notify(new InboundPayload(p.Bytes));
                                    nextState = Stay();
                                })
                                .Default(msg =>
                                {
                                    throw new AkkaProtocolException(
                                        string.Format("Unhandled message in state Open(InboundPayload) with type {0}",
                                            msg));
                                }))
                            .Default(d => nextState = Stay());
                    })
                    .With<HeartbeatTimer>(hrt => @event.StateData.Match()
                        .With<AssociatedWaitHandler>(awh => nextState = HandleTimers(awh.WrappedHandle))
                        .With<ListenerReady>(lr => nextState = HandleTimers(lr.WrappedHandle)))
                    .With<DisassociateUnderlying>(du =>
                    {
                        AssociationHandle handle = null;
                        @event.StateData.Match()
                            .With<ListenerReady>(lr => handle = lr.WrappedHandle)
                            .With<AssociatedWaitHandler>(awh => handle = awh.WrappedHandle)
                            .Default(
                                msg =>
                                {
                                    throw new AkkaProtocolException(
                                        string.Format(
                                            "unhandled message in state Open(DisassociateUnderlying) with type {0}", msg));

                                });
                        SendDisassociate(handle, du.Info);
                        nextState = Stop();
                    })
                    .With<HandleListenerRegistered>(hlr => @event.StateData.Match()
                        .With<AssociatedWaitHandler>(awh =>
                        {
                            foreach (var msg in awh.Queue)
                                hlr.Listener.Notify(new InboundPayload(msg));
                            nextState = Stay().Using(new ListenerReady(hlr.Listener, awh.WrappedHandle));
                        }));

                return nextState;
            });

            OnTermination(@event => @event.StateData.Match()
                .With<OutboundUnassociated>(ou => ou.StatusCompletionSource.TrySetException(@event.Reason is Failure
                    ? new AkkaProtocolException(@event.Reason.ToString())
                    : new AkkaProtocolException("Transport disassociated before handshake finished")))
                .With<OutboundUnderlyingAssociated>(oua =>
                {
                    Exception associationFailure = null;
                    @event.Reason.Match()
                        .With<Failure>(f => f.Cause.Match()
                            .With<TimeoutReason>(
                                timeout =>
                                    associationFailure =
                                        new AkkaProtocolException("No reponse from remote. Handshake timed out."))
                            .With<ForbiddenUidReason>(
                                forbidden =>
                                    associationFailure =
                                        new AkkaProtocolException(
                                            "The remote system has a UID that has been quarantined. Association aborted."))
                            .With<DisassociateInfo>(info => associationFailure = DisassociateException(info))
                            .Default(
                                msg =>
                                    associationFailure =
                                        new AkkaProtocolException(
                                            "Transport disassociated before handshake finished")));

                    oua.StatusCompletionSource.TrySetException(associationFailure);
                    oua.WrappedHandle.Disassociate();
                })
                .With<AssociatedWaitHandler>(awh =>
                {
                    Disassociated disassociateNotification = null;
                    if (@event.Reason is Failure && ((Failure)@event.Reason).Cause is DisassociateInfo)
                    {
                        disassociateNotification =
                            new Disassociated(((Failure)@event.Reason).Cause.AsInstanceOf<DisassociateInfo>());
                    }
                    else
                    {
                        disassociateNotification = new Disassociated(DisassociateInfo.Unknown);
                    }
                    awh.HandlerListener.ContinueWith(result => result.Result.Notify(disassociateNotification),
                        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.AttachedToParent);
                })
                .With<ListenerReady>(lr =>
                {
                    Disassociated disassociateNotification = null;
                    if (@event.Reason is Failure && ((Failure)@event.Reason).Cause is DisassociateInfo)
                    {
                        disassociateNotification =
                            new Disassociated(((Failure)@event.Reason).Cause.AsInstanceOf<DisassociateInfo>());
                    }
                    else
                    {
                        disassociateNotification = new Disassociated(DisassociateInfo.Unknown);
                    }
                    lr.Listener.Notify(disassociateNotification);
                    lr.WrappedHandle.Disassociate();
                })
                .With<InboundUnassociated>(iu =>
                    iu.WrappedHandle.Disassociate()));

            _initialData.Match()
                .With<OutboundUnassociated>(d =>
                {
                    d.Transport.Associate(d.RemoteAddress).PipeTo(Self);
                    StartWith(AssociationState.Closed, d);
                })
                .With<InboundUnassociated>(d =>
                {
                    d.WrappedHandle.ReadHandlerSource.SetResult(new ActorHandleEventListener(Self));
                    StartWith(AssociationState.WaitHandshake, d);
                });

        }

        #endregion

        #region Actor methods

        protected override void PostStop()
        {
            CancelTimer("heartbeat-timer");
            base.PostStop(); //pass to OnTermination
        }

        #endregion

        #region Internal protocol messaging methods

        private Exception DisassociateException(DisassociateInfo info)
        {
            switch (info)
            {
                case DisassociateInfo.Shutdown:
                    return new AkkaProtocolException("The remote system refused the association because it is shutting down.");
                case DisassociateInfo.Quarantined:
                    return new AkkaProtocolException("The remote system has quarantined this system. No further associations to the remote systems are possible until this system is restarted.");
                case DisassociateInfo.Unknown:
                default:
                    return new AkkaProtocolException("The remote system explicitly disassociated (reason unknown).");
            }
        }

        private State<AssociationState, ProtocolStateData> HandleTimers(AssociationHandle wrappedHandle)
        {
            if (_failureDetector.IsAvailable)
            {
                SendHeartBeat(wrappedHandle);
                return Stay();
            }
            else
            {
                SendDisassociate(wrappedHandle, DisassociateInfo.Unknown);
                return Stop(new Failure(new TimeoutReason()));
            }
        }

        private void ListenForListenerRegistration(TaskCompletionSource<IHandleEventListener> readHandlerSource)
        {
            readHandlerSource.Task.ContinueWith(rh => new HandleListenerRegistered(rh.Result),
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.AttachedToParent).PipeTo(Self);
        }

        private Task<IHandleEventListener> NotifyOutboundHandler(AssociationHandle wrappedHandle,
            HandshakeInfo handshakeInfo, TaskCompletionSource<AssociationHandle> statusPromise)
        {
            var readHandlerPromise = new TaskCompletionSource<IHandleEventListener>();
            ListenForListenerRegistration(readHandlerPromise);

            statusPromise.SetResult(new AkkaProtocolHandle(_localAddress, wrappedHandle.RemoteAddress, readHandlerPromise, wrappedHandle, handshakeInfo, Self, _codec));

            return readHandlerPromise.Task;
        }

        private Task<IHandleEventListener> NotifyInboundHandler(AssociationHandle wrappedHandle,
            HandshakeInfo handshakeInfo, IAssociationEventListener associationEventListener)
        {
            var readHandlerPromise = new TaskCompletionSource<IHandleEventListener>();
            ListenForListenerRegistration(readHandlerPromise);

            associationEventListener.Notify(
                new InboundAssociation(
                    new AkkaProtocolHandle(_localAddress, handshakeInfo.Origin, readHandlerPromise, wrappedHandle, handshakeInfo, Self, _codec)));
            return readHandlerPromise.Task;
        }

        private IAkkaPdu DecodePdu(ByteString pdu)
        {
            try
            {
                return _codec.DecodePdu(pdu);
            }
            catch (Exception ex)
            {
                throw new AkkaProtocolException(
                    string.Format("Error while decoding incoming Akka PDU of length {0}", pdu.Length), ex);
            }
        }

        private void InitTimers()
        {
            SetTimer("heartbeat-timer", new HeartbeatTimer(), _settings.TransportHeartBeatInterval, true);
        }

        private bool SendAssociate(AssociationHandle wrappedHandle, HandshakeInfo info)
        {
            try
            {
                return wrappedHandle.Write(_codec.ConstructAssociate(info));
            }
            catch (Exception ex)
            {
                throw new AkkaProtocolException("Error writing ASSOCIATE to transport", ex);
            }
        }

        private void SendDisassociate(AssociationHandle wrappedHandle, DisassociateInfo info)
        {
            try
            {
                wrappedHandle.Write(_codec.ConstructDisassociate(info));
            }
            catch (Exception ex)
            {
                throw new AkkaProtocolException("Error writing DISASSOCIATE to transport", ex);
            }
        }

        private void SendHeartBeat(AssociationHandle wrappedHandle)
        {
            try
            {
                wrappedHandle.Write(_codec.ConstructHeartbeat());
            }
            catch (Exception ex)
            {
                throw new AkkaProtocolException("Error writing HEARTBEAT to transport", ex);
            }
        }

        #endregion

        #region Static methods

        public static Props OutboundProps(HandshakeInfo handshakeInfo, Address remoteAddress,
            TaskCompletionSource<AssociationHandle> statusCompletionSource,
            Transport transport, AkkaProtocolSettings settings, AkkaPduCodec codec, FailureDetector failureDetector, int? refuseUid = null)
        {
            return Props.Create(() => new ProtocolStateActor(handshakeInfo, remoteAddress, statusCompletionSource, transport, settings, codec, failureDetector, refuseUid));
        }

        public static Props InboundProps(HandshakeInfo handshakeInfo, AssociationHandle wrappedHandle,
            IAssociationEventListener associationEventListener, AkkaProtocolSettings settings, AkkaPduCodec codec, FailureDetector failureDetector)
        {
            return
                Props.Create(
                    () =>
                        new ProtocolStateActor(handshakeInfo, wrappedHandle, associationEventListener, settings, codec, failureDetector));
        }

        #endregion
    }
}