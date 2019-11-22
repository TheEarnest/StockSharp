#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Algo.Algo
File: BasketMessageAdapter.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.Algo
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Collections;
	using Ecng.Common;
	using Ecng.Serialization;

	using MoreLinq;

	using StockSharp.Algo.Candles;
	using StockSharp.Algo.Candles.Compression;
	using StockSharp.Algo.Commissions;
	using StockSharp.Algo.Latency;
	using StockSharp.Algo.PnL;
	using StockSharp.Algo.Slippage;
	using StockSharp.Algo.Storages;
	using StockSharp.Algo.Testing;
	using StockSharp.Logging;
	using StockSharp.Messages;
	using StockSharp.Localization;

	/// <summary>
	/// The interface describing the list of adapters to trading systems with which the aggregator operates.
	/// </summary>
	public interface IInnerAdapterList : ISynchronizedCollection<IMessageAdapter>, INotifyList<IMessageAdapter>
	{
		/// <summary>
		/// Internal adapters sorted by operation speed.
		/// </summary>
		IEnumerable<IMessageAdapter> SortedAdapters { get; }

		/// <summary>
		/// The indexer through which speed priorities (the smaller the value, then adapter is faster) for internal adapters are set.
		/// </summary>
		/// <param name="adapter">The internal adapter.</param>
		/// <returns>The adapter priority. If the -1 value is set the adapter is considered to be off.</returns>
		int this[IMessageAdapter adapter] { get; set; }
	}

	/// <summary>
	/// Adapter-aggregator that allows simultaneously to operate multiple adapters connected to different trading systems.
	/// </summary>
	public class BasketMessageAdapter : BaseLogReceiver, IMessageAdapter
	{
		private sealed class InnerAdapterList : CachedSynchronizedList<IMessageAdapter>, IInnerAdapterList
		{
			private readonly BasketMessageAdapter _parent;
			private readonly Dictionary<IMessageAdapter, int> _enables = new Dictionary<IMessageAdapter, int>();

			public InnerAdapterList(BasketMessageAdapter parent)
			{
				_parent = parent ?? throw new ArgumentNullException(nameof(parent));
			}

			public IEnumerable<IMessageAdapter> SortedAdapters => Cache.Where(t => this[t] != -1).OrderBy(t => this[t]);

			protected override bool OnAdding(IMessageAdapter item)
			{
				_enables.Add(item, 0);
				return base.OnAdding(item);
			}

			protected override bool OnInserting(int index, IMessageAdapter item)
			{
				_enables.Add(item, 0);
				return base.OnInserting(index, item);
			}

			protected override bool OnRemoving(IMessageAdapter item)
			{
				_enables.Remove(item);
				_parent._activeAdapters.Remove(item);

				lock (_parent._connectedResponseLock)
					_parent._adapterStates.Remove(item);

				return base.OnRemoving(item);
			}

			protected override bool OnClearing()
			{
				_enables.Clear();
				_parent._activeAdapters.Clear();

				lock (_parent._connectedResponseLock)
					_parent._adapterStates.Clear();

				return base.OnClearing();
			}

			public int this[IMessageAdapter adapter]
			{
				get
				{
					lock (SyncRoot)
						return _enables.TryGetValue2(adapter) ?? -1;
				}
				set
				{
					if (value < -1)
						throw new ArgumentOutOfRangeException();

					lock (SyncRoot)
					{
						if (!Contains(adapter))
							Add(adapter);

						_enables[adapter] = value;
						//_portfolioTraders.Clear();
					}
				}
			}
		}

		private class SubscriptionKey : Tuple<DataType, SecurityId?>
		{
			public SubscriptionKey(ISubscriptionMessage subscrMsg)
				: base(GetDataType(subscrMsg), GetSecurityId(subscrMsg))
			{
			}

			private static DataType GetDataType(ISubscriptionMessage subscrMsg)
			{
				if (subscrMsg == null)
					throw new ArgumentNullException(nameof(subscrMsg));

				if (subscrMsg is MarketDataMessage mdMsg)
					return mdMsg.DataType.ToDataType(mdMsg.Arg);
				else if (subscrMsg is PortfolioLookupMessage)
					return DataType.PositionChanges;
				else if (subscrMsg is OrderStatusMessage)
					return DataType.Transactions;
				else if (subscrMsg is BoardLookupMessage)
					return DataType.Board;
				else if (subscrMsg is SecurityLookupMessage)
					return DataType.Securities;

				throw new ArgumentException(subscrMsg.ToString());
			}

			private static SecurityId? GetSecurityId(ISubscriptionMessage subscrMsg)
			{
				if (subscrMsg == null)
					throw new ArgumentNullException(nameof(subscrMsg));

				if (subscrMsg is ISecurityIdMessage secIdMsg)
					return secIdMsg.SecurityId;
				else if (subscrMsg is INullableSecurityIdMessage nullSecIdMsg)
					return nullSecIdMsg.SecurityId;

				return null;
			}
		}

		private class ParentChildMap
		{
			private readonly SyncObject _syncObject = new SyncObject();
			private readonly Dictionary<long, RefTriple<long, bool?, IMessageAdapter>> _childToParentIds = new Dictionary<long, RefTriple<long, bool?, IMessageAdapter>>();

			public void AddMapping(long childId, long parentId, IMessageAdapter adapter)
			{
				if (adapter == null)
					throw new ArgumentNullException(nameof(adapter));

				lock (_syncObject)
					_childToParentIds.Add(childId, RefTuple.Create(parentId, (bool?)null, adapter));
			}

			public IDictionary<long, IMessageAdapter> GetChild(long parentId)
			{
				lock (_syncObject)
					return FilterByParent(parentId).Where(p => p.Value.Second == true).ToDictionary(p => p.Key, p => p.Value.Third);
			}

			private IEnumerable<KeyValuePair<long, RefTriple<long, bool?, IMessageAdapter>>> FilterByParent(long parentId) => _childToParentIds.Where(p => p.Value.First == parentId);

			public long? ProcessChildResponse(long childId, bool isOk, out bool allError)
			{
				allError = true;

				lock (_syncObject)
				{
					if (!_childToParentIds.TryGetValue(childId, out var tuple))
						return null;
					
					var parentId = tuple.First;
					tuple.Second = isOk;

					foreach (var pair in FilterByParent(parentId))
					{
						var t = pair.Value;

						// one of adapter still not yet response.
						if (t.Second == null)
							return null;
						
						if (t.Second == true)
							allError = false;
					}

					return parentId;
				}
			}

			public void Clear()
			{
				lock (_syncObject)
					_childToParentIds.Clear();
			}

			public bool IsChild(long childId)
			{
				lock (_syncObject)
					return _childToParentIds.ContainsKey(childId);
			}

			public RefTriple<long, bool?, IMessageAdapter> RemoveByChild(long childId)
			{
				lock (_syncObject)
					return _childToParentIds.TryGetAndRemove(childId);
			}
		}

		private readonly Dictionary<long, HashSet<IMessageAdapter>> _nonSupportedAdapters = new Dictionary<long, HashSet<IMessageAdapter>>();
		private readonly CachedSynchronizedDictionary<IMessageAdapter, IMessageAdapter> _activeAdapters = new CachedSynchronizedDictionary<IMessageAdapter, IMessageAdapter>();
		private readonly SyncObject _connectedResponseLock = new SyncObject();
		private readonly Dictionary<MessageTypes, CachedSynchronizedSet<IMessageAdapter>> _messageTypeAdapters = new Dictionary<MessageTypes, CachedSynchronizedSet<IMessageAdapter>>();
		private readonly HashSet<IMessageAdapter> _pendingConnectAdapters = new HashSet<IMessageAdapter>();
		private readonly Queue<Message> _pendingMessages = new Queue<Message>();
		private readonly HashSet<IMessageAdapter> _connectedAdapters = new HashSet<IMessageAdapter>();
		
		private readonly Dictionary<IMessageAdapter, ConnectionStates> _adapterStates = new Dictionary<IMessageAdapter, ConnectionStates>();
		private ConnectionStates _currState = ConnectionStates.Disconnected;

		private readonly SynchronizedDictionary<string, IMessageAdapter> _portfolioAdapters = new SynchronizedDictionary<string, IMessageAdapter>(StringComparer.InvariantCultureIgnoreCase);
		private readonly SynchronizedDictionary<Tuple<SecurityId, MarketDataTypes?>, IMessageAdapter> _securityAdapters = new SynchronizedDictionary<Tuple<SecurityId, MarketDataTypes?>, IMessageAdapter>();

		private readonly SynchronizedDictionary<long, Tuple<ISubscriptionMessage, IMessageAdapter>> _requestsById = new SynchronizedDictionary<long, Tuple<ISubscriptionMessage, IMessageAdapter>>();
		private readonly SynchronizedPairSet<SubscriptionKey, long> _keysToTransId = new SynchronizedPairSet<SubscriptionKey, long>();
		private readonly SynchronizedSet<long> _subscriptionListRequests = new SynchronizedSet<long>();
		private readonly SynchronizedDictionary<IMessageAdapter, HashSet<string>> _subscribedPortfolios = new SynchronizedDictionary<IMessageAdapter, HashSet<string>>();
		private readonly ParentChildMap _parentChildMap = new ParentChildMap();

		/// <summary>
		/// Initializes a new instance of the <see cref="BasketMessageAdapter"/>.
		/// </summary>
		/// <param name="transactionIdGenerator">Transaction id generator.</param>
		/// <param name="candleBuilderProvider">Candle builders provider.</param>
		public BasketMessageAdapter(IdGenerator transactionIdGenerator, CandleBuilderProvider candleBuilderProvider)
			: this(transactionIdGenerator, new InMemorySecurityMessageAdapterProvider(), new InMemoryPortfolioMessageAdapterProvider(), candleBuilderProvider, null, null)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="BasketMessageAdapter"/>.
		/// </summary>
		/// <param name="transactionIdGenerator">Transaction id generator.</param>
		/// <param name="securityAdapterProvider">The security based message adapter's provider.</param>
		/// <param name="portfolioAdapterProvider">The portfolio based message adapter's provider.</param>
		/// <param name="candleBuilderProvider">Candle builders provider.</param>
		/// <param name="storageRegistry">The storage of market data.</param>
		/// <param name="snapshotRegistry">Snapshot storage registry.</param>
		public BasketMessageAdapter(IdGenerator transactionIdGenerator,
			ISecurityMessageAdapterProvider securityAdapterProvider,
			IPortfolioMessageAdapterProvider portfolioAdapterProvider,
			CandleBuilderProvider candleBuilderProvider,
			IStorageRegistry storageRegistry,
			SnapshotRegistry snapshotRegistry)

		{
			_transactionIdGenerator = transactionIdGenerator ?? throw new ArgumentNullException(nameof(transactionIdGenerator));
			_innerAdapters = new InnerAdapterList(this);
			SecurityAdapterProvider = securityAdapterProvider ?? throw new ArgumentNullException(nameof(securityAdapterProvider));
			PortfolioAdapterProvider = portfolioAdapterProvider ?? throw new ArgumentNullException(nameof(portfolioAdapterProvider));
			CandleBuilderProvider = candleBuilderProvider ?? throw new ArgumentNullException(nameof(portfolioAdapterProvider));

			StorageRegistry = storageRegistry;// ?? throw new ArgumentNullException(nameof(storageRegistry));
			SnapshotRegistry = snapshotRegistry;// ?? throw new ArgumentNullException(nameof(snapshotRegistry));

			LatencyManager = new LatencyManager();
			CommissionManager = new CommissionManager();
			//PnLManager = new PnLManager();
			SlippageManager = new SlippageManager();

			SecurityAdapterProvider.Changed += SecurityAdapterProviderOnChanged;
			PortfolioAdapterProvider.Changed += PortfolioAdapterProviderOnChanged;
		}

		/// <summary>
		/// The portfolio based message adapter's provider.
		/// </summary>
		public IPortfolioMessageAdapterProvider PortfolioAdapterProvider { get; }

		/// <summary>
		/// The security based message adapter's provider.
		/// </summary>
		public ISecurityMessageAdapterProvider SecurityAdapterProvider { get; }

		/// <summary>
		/// Candle builders provider.
		/// </summary>
		public CandleBuilderProvider CandleBuilderProvider { get; }

		private readonly InnerAdapterList _innerAdapters;

		/// <summary>
		/// Adapters with which the aggregator operates.
		/// </summary>
		public IInnerAdapterList InnerAdapters => _innerAdapters;

		private INativeIdStorage _nativeIdStorage = new InMemoryNativeIdStorage();

		/// <summary>
		/// Security native identifier storage.
		/// </summary>
		public INativeIdStorage NativeIdStorage
		{
			get => _nativeIdStorage;
			set => _nativeIdStorage = value ?? throw new ArgumentNullException(nameof(value));
		}

		private ISecurityMappingStorage _securityMappingStorage;

		/// <summary>
		/// Security identifier mappings storage.
		/// </summary>
		public ISecurityMappingStorage SecurityMappingStorage
		{
			get => _securityMappingStorage;
			set => _securityMappingStorage = value ?? throw new ArgumentNullException(nameof(value));
		}

		/// <summary>
		/// Extended info <see cref="Message.ExtensionInfo"/> storage.
		/// </summary>
		public IExtendedInfoStorage ExtendedInfoStorage { get; set; }

		/// <summary>
		/// Orders registration delay calculation manager.
		/// </summary>
		public ILatencyManager LatencyManager { get; set; }

		/// <summary>
		/// The profit-loss manager.
		/// </summary>
		public IPnLManager PnLManager { get; set; }

		/// <summary>
		/// The commission calculating manager.
		/// </summary>
		public ICommissionManager CommissionManager { get; set; }

		/// <summary>
		/// Slippage manager.
		/// </summary>
		public ISlippageManager SlippageManager { get; set; }

		private readonly IdGenerator _transactionIdGenerator;

		IdGenerator IMessageAdapter.TransactionIdGenerator => _transactionIdGenerator;

		IEnumerable<MessageTypeInfo> IMessageAdapter.PossibleSupportedMessages
		{
			get => GetSortedAdapters().SelectMany(a => a.PossibleSupportedMessages).DistinctBy(i => i.Type);
			set { }
		}

		IEnumerable<MessageTypes> IMessageAdapter.SupportedMessages
		{
			get => GetSortedAdapters().SelectMany(a => a.SupportedMessages).Distinct();
			set { }
		}

		IEnumerable<MessageTypes> IMessageAdapter.SupportedOutMessages
		{
			get => GetSortedAdapters().SelectMany(a => a.SupportedOutMessages).Distinct();
			set { }
		}

		/// <inheritdoc />
		IEnumerable<MarketDataTypes> IMessageAdapter.SupportedMarketDataTypes
		{
			get => GetSortedAdapters().SelectMany(a => a.SupportedMarketDataTypes).Distinct();
			set { }
		}

		// TODO
		IDictionary<string, RefPair<SecurityTypes, string>> IMessageAdapter.SecurityClassInfo => null;

		IEnumerable<Level1Fields> IMessageAdapter.CandlesBuildFrom => GetSortedAdapters().SelectMany(a => a.CandlesBuildFrom).Distinct();

		bool IMessageAdapter.CheckTimeFrameByRequest => false;

		ReConnectionSettings IMessageAdapter.ReConnectionSettings { get; } = new ReConnectionSettings();

		TimeSpan IMessageAdapter.HeartbeatInterval { get; set; }

		string IMessageAdapter.StorageName => nameof(BasketMessageAdapter).Remove(nameof(MessageAdapter));

		bool IMessageAdapter.IsNativeIdentifiersPersistable => false;

		bool IMessageAdapter.IsNativeIdentifiers => false;

		bool IMessageAdapter.IsFullCandlesOnly => GetSortedAdapters().All(a => a.IsFullCandlesOnly);

		bool IMessageAdapter.IsSupportSubscriptions => true;

		bool IMessageAdapter.IsSupportSubscriptionBySecurity => true;

		bool IMessageAdapter.IsSupportCandlesUpdates => GetSortedAdapters().Any(a => a.IsSupportCandlesUpdates);

		IEnumerable<Tuple<string, Type>> IMessageAdapter.SecurityExtendedFields => GetSortedAdapters().SelectMany(a => a.SecurityExtendedFields).Distinct();

		bool IMessageAdapter.IsSupportSecuritiesLookupAll => GetSortedAdapters().Any(a => a.IsSupportSecuritiesLookupAll);

		IEnumerable<int> IMessageAdapter.SupportedOrderBookDepths => GetSortedAdapters().SelectMany(a => a.SupportedOrderBookDepths).Distinct().OrderBy();

		bool IMessageAdapter.IsSupportOrderBookIncrements => GetSortedAdapters().Any(a => a.IsSupportOrderBookIncrements);

		bool IMessageAdapter.IsSupportExecutionsPnL => GetSortedAdapters().Any(a => a.IsSupportExecutionsPnL);

		MessageAdapterCategories IMessageAdapter.Categories => GetSortedAdapters().Select(a => a.Categories).JoinMask();

		OrderCancelVolumeRequireTypes? IMessageAdapter.OrderCancelVolumeRequired => GetSortedAdapters().FirstOrDefault()?.OrderCancelVolumeRequired;

		/// <inheritdoc />
		public string AssociatedBoardCode => MessageAdapter.DefaultAssociatedBoardCode;

		Type IMessageAdapter.OrderConditionType => null;

		OrderCondition IMessageAdapter.CreateOrderCondition() => null;

		IOrderLogMarketDepthBuilder IMessageAdapter.CreateOrderLogMarketDepthBuilder(SecurityId securityId)
			=> new OrderLogMarketDepthBuilder(securityId);

		IEnumerable<object> IMessageAdapter.GetCandleArgs(Type candleType, SecurityId securityId, DateTimeOffset? from, DateTimeOffset? to)
			=> GetSortedAdapters().SelectMany(a => a.GetCandleArgs(candleType, securityId, from, to)).Distinct().OrderBy();

		TimeSpan IMessageAdapter.GetHistoryStepSize(DataType dataType, out TimeSpan iterationInterval)
		{
			foreach (var adapter in GetSortedAdapters())
			{
				var step = adapter.GetHistoryStepSize(dataType, out iterationInterval);

				if (step > TimeSpan.Zero)
					return step;
			}

			iterationInterval = TimeSpan.Zero;
			return TimeSpan.Zero;
		}

		bool IMessageAdapter.IsAllDownloadingSupported(MarketDataTypes dataType) => GetSortedAdapters().Any(a => a.IsAllDownloadingSupported(dataType));

		/// <inheritdoc />
		public bool IsSecurityNewsOnly => GetSortedAdapters().All(a => a.IsSecurityNewsOnly);

		/// <summary>
		/// Restore subscription on reconnect.
		/// </summary>
		/// <remarks>
		/// Error case like connection lost etc.
		/// </remarks>
		public bool IsRestoreSubscriptionOnErrorReconnect { get; set; } = true;

		/// <summary>
		/// Restore subscription on reconnect.
		/// </summary>
		/// <remarks>
		/// Normal case connect/disconnect.
		/// </remarks>
		public bool IsRestoreSubscriptionOnNormalReconnect { get; set; } = true;

		/// <summary>
		/// Send unsubscribe on disconnect command.
		/// </summary>
		public bool IsAutoUnSubscribeOnDisconnect { get; set; } = true;

		/// <summary>
		/// Suppress reconnecting errors.
		/// </summary>
		public bool SuppressReconnectingErrors { get; set; } = true;

		/// <summary>
		/// Use <see cref="CandleBuilderMessageAdapter"/>.
		/// </summary>
		public bool SupportCandlesCompression { get; set; } = true;

		/// <summary>
		/// Use <see cref="OrderLogMessageAdapter"/>.
		/// </summary>
		public bool SupportBuildingFromOrderLog { get; set; } = true;

		/// <summary>
		/// Use <see cref="OrderBookTruncateMessageAdapter"/>.
		/// </summary>
		public bool SupportOrderBookTruncate { get; set; } = true;

		/// <summary>
		/// Use <see cref="OfflineMessageAdapter"/>.
		/// </summary>
		public bool SupportOffline { get; set; }

		/// <summary>
		/// Do not add extra adapters.
		/// </summary>
		public bool IgnoreExtraAdapters { get; set; }

		/// <summary>
		/// To call the <see cref="ConnectMessage"/> event when the first adapter connects to <see cref="InnerAdapters"/>.
		/// </summary>
		public bool ConnectDisconnectEventOnFirstAdapter { get; set; } = true;

		/// <summary>
		/// The storage of market data.
		/// </summary>
		public IStorageRegistry StorageRegistry { get; }

		/// <summary>
		/// Snapshot storage registry.
		/// </summary>
		public SnapshotRegistry SnapshotRegistry { get; }

		/// <summary>
		/// Save data only for subscriptions.
		/// </summary>
		public bool StorageFilterSubscription { get; set; }

		/// <summary>
		/// The storage (database, file etc.).
		/// </summary>
		public IMarketDataDrive StorageDrive { get; set; }

		/// <summary>
		/// Storage mode. By default is <see cref="StorageModes.Incremental"/>.
		/// </summary>
		public StorageModes StorageMode { get; set; } = StorageModes.Incremental;

		/// <summary>
		/// Format.
		/// </summary>
		public StorageFormats StorageFormat { get; set; } = StorageFormats.Binary;

		private TimeSpan _storageDaysLoad;

		/// <summary>
		/// Max days to load stored data.
		/// </summary>
		public TimeSpan StorageDaysLoad
		{
			get => _storageDaysLoad;
			set
			{
				if (value < TimeSpan.Zero)
					throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.Str1219);

				_storageDaysLoad = value;
			}
		}

		/// <summary>
		/// Use separated <see cref="IMessageChannel"/> for each adapters.
		/// </summary>
		public bool UseSeparatedChannels { get; set; }

		/// <summary>
		/// To get adapters <see cref="IInnerAdapterList.SortedAdapters"/> sorted by the specified priority. By default, there is no sorting.
		/// </summary>
		/// <returns>Sorted adapters.</returns>
		protected IEnumerable<IMessageAdapter> GetSortedAdapters() => _innerAdapters.SortedAdapters;

		private IMessageAdapter[] ActiveAdapters => _activeAdapters.CachedValues;

		private void ProcessReset(ResetMessage message, bool clearSubscriptions)
		{
			ActiveAdapters.ForEach(a =>
			{
				a.SendInMessage(message);
				a.Dispose();
			});

			_activeAdapters.Clear();

			lock (_connectedResponseLock)
			{
				_connectedAdapters.Clear();
				_messageTypeAdapters.Clear();
				_pendingConnectAdapters.Clear();
				_pendingMessages.Clear();
				_nonSupportedAdapters.Clear();

				_adapterStates.Clear();
				_currState = ConnectionStates.Disconnected;
			}

			if (clearSubscriptions)
			{
				_keysToTransId.Clear();
				_requestsById.Clear();
				_parentChildMap.Clear();
				_subscriptionListRequests.Clear();
				_subscribedPortfolios.Clear();
			}
		}

		private IMessageAdapter CreateWrappers(IMessageAdapter adapter)
		{
			if (UseSeparatedChannels)
			{
				adapter = new ChannelMessageAdapter(adapter,
					new InMemoryMessageChannel($"{adapter} In", SendOutError), 
					new InMemoryMessageChannel($"{adapter} Out", SendOutError))
				{
					OwnInnerAdapter = true,
				};
			}

			if (LatencyManager != null)
			{
				adapter = new LatencyMessageAdapter(adapter) { LatencyManager = LatencyManager.Clone(), OwnInnerAdapter = true };
			}

			if (SlippageManager != null)
			{
				adapter = new SlippageMessageAdapter(adapter) { SlippageManager = SlippageManager.Clone(), OwnInnerAdapter = true };
			}

			if (adapter.IsNativeIdentifiers)
			{
				adapter = new SecurityNativeIdMessageAdapter(adapter, NativeIdStorage) { OwnInnerAdapter = true };
			}

			if (SecurityMappingStorage != null)
			{
				adapter = new SecurityMappingMessageAdapter(adapter, SecurityMappingStorage) { OwnInnerAdapter = true };
			}

			if (PnLManager != null && !adapter.IsSupportExecutionsPnL)
			{
				adapter = new PnLMessageAdapter(adapter) { PnLManager = PnLManager.Clone(), OwnInnerAdapter = true };
			}

			if (CommissionManager != null)
			{
				adapter = new CommissionMessageAdapter(adapter) { CommissionManager = CommissionManager.Clone(), OwnInnerAdapter = true };
			}

			if (adapter.IsSupportSubscriptions)
			{
				adapter = new SubscriptionIdMessageAdapter(adapter) { OwnInnerAdapter = true };
			}

			adapter = new PartialDownloadMessageAdapter(adapter) { OwnInnerAdapter = true };

			if (adapter.IsFullCandlesOnly)
			{
				adapter = new CandleHolderMessageAdapter(adapter) { OwnInnerAdapter = true };
			}

			adapter = new LookupTrackingMessageAdapter(adapter) { OwnInnerAdapter = true };

			if (StorageRegistry != null)
			{
				adapter = new StorageMessageAdapter(adapter, StorageRegistry, SnapshotRegistry, CandleBuilderProvider)
				{
					OwnInnerAdapter = true,

					FilterSubscription = StorageFilterSubscription,
					Drive = StorageDrive,
					DaysLoad = StorageDaysLoad,
					Format = StorageFormat,
					Mode = StorageMode,
				};
			}

			if (SupportBuildingFromOrderLog)
			{
				adapter = new OrderLogMessageAdapter(adapter) { OwnInnerAdapter = true };
			}

			if (adapter.IsSupportOrderBookIncrements)
			{
				adapter = new OrderBookInrementMessageAdapter(adapter) { OwnInnerAdapter = true };
			}

			if (SupportOrderBookTruncate)
			{
				adapter = new OrderBookTruncateMessageAdapter(adapter) { OwnInnerAdapter = true };
			}

			if (SupportCandlesCompression)
			{
				adapter = new CandleBuilderMessageAdapter(adapter, CandleBuilderProvider) { OwnInnerAdapter = true };
			}

			if (ExtendedInfoStorage != null && !adapter.SecurityExtendedFields.IsEmpty())
			{
				adapter = new ExtendedInfoStorageMessageAdapter(adapter, ExtendedInfoStorage) { OwnInnerAdapter = true };
			}

			return adapter;
		}

		private readonly Dictionary<IMessageAdapter, bool> _hearbeatFlags = new Dictionary<IMessageAdapter, bool>();

		private bool IsHeartbeatOn(IMessageAdapter adapter)
		{
			return _hearbeatFlags.TryGetValue2(adapter) ?? true;
		}

		/// <summary>
		/// Apply on/off heartbeat mode for the specified adapter.
		/// </summary>
		/// <param name="adapter">Adapter.</param>
		/// <param name="on">Is active.</param>
		public void ApplyHeartbeat(IMessageAdapter adapter, bool on)
		{
			_hearbeatFlags[adapter] = on;
		}

		private IMessageAdapter GetUnderlyingAdapter(IMessageAdapter adapter)
		{
			if (adapter == null)
				throw new ArgumentNullException(nameof(adapter));

			if (adapter is IMessageAdapterWrapper wrapper)
			{
				return wrapper is IRealTimeEmulationMarketDataAdapter || wrapper is IHistoryMessageAdapter
					? wrapper
					: GetUnderlyingAdapter(wrapper.InnerAdapter);
			}

			return adapter;
		}

		/// <inheritdoc />
		void IMessageChannel.SendInMessage(Message message)
		{
			OnSendInMessage(message);
		}

		/// <summary>
		/// Send message.
		/// </summary>
		/// <param name="message">Message.</param>
		protected virtual void OnSendInMessage(Message message)
		{
			if (message is ITransactionIdMessage transIdMsg && transIdMsg.TransactionId == 0)
				throw new ArgumentException(message.ToString());

			if (message.IsBack)
			{
				var adapter = message.Adapter;

				if (adapter == null)
					throw new InvalidOperationException();

				if (adapter == this)
				{
					message.Adapter = null;
					message.IsBack = false;
				}
				else
				{
					ProcessAdapterMessage(adapter, message);
					return;	
				}
			}

			switch (message.Type)
			{
				case MessageTypes.Reset:
					ProcessReset((ResetMessage)message, true);
					break;

				case MessageTypes.Connect:
				{
					ProcessReset(new ResetMessage(), !IsRestoreSubscriptionOnNormalReconnect);

					_currState = ConnectionStates.Connecting;

					_activeAdapters.AddRange(GetSortedAdapters().ToDictionary(a => a, a =>
					{
						lock (_connectedResponseLock)
						{
							_pendingConnectAdapters.Add(a);
							_adapterStates.Add(a, ConnectionStates.Connecting);
						}

						var adapter = IgnoreExtraAdapters ? a : CreateWrappers(a);

						if (IsHeartbeatOn(a))
						{
							adapter = new HeartbeatMessageAdapter(adapter)
							{
								SuppressReconnectingErrors = SuppressReconnectingErrors,
								Parent = this,
								OwnInnerAdapter = adapter != a
							};
						}

						if (SupportOffline)
							adapter = new OfflineMessageAdapter(adapter) { OwnInnerAdapter = adapter != a };

						adapter.NewOutMessage += m => OnInnerAdapterNewOutMessage(adapter, m);
						
						return adapter;
					}));
					
					if (ActiveAdapters.Length == 0)
						throw new InvalidOperationException(LocalizedStrings.Str3650);

					ActiveAdapters.ForEach(a =>
					{
						var u = GetUnderlyingAdapter(a);
						this.AddInfoLog("Connecting '{0}'.", u);

						a.SendInMessage(message);
					});

					break;
				}

				case MessageTypes.Disconnect:
				{
					IDictionary<IMessageAdapter, IMessageAdapter> adapters;

					lock (_connectedResponseLock)
					{
						_currState = ConnectionStates.Disconnecting;

						adapters = _connectedAdapters.ToDictionary(a => a, a =>
						{
							var u = GetUnderlyingAdapter(a);

							var prevState = _adapterStates.TryGetValue2(u);

							if (prevState != ConnectionStates.Failed)
								_adapterStates[u] = ConnectionStates.Disconnecting;

							return u;
						});
					}

					foreach (var a in adapters)
					{
						var adapter = a.Key;
						var u = a.Value;

						this.AddInfoLog("Disconnecting '{0}'.", u);

						if (IsAutoUnSubscribeOnDisconnect)
						{
							var unsubscribes = new List<Message>();

							lock (_requestsById.SyncRoot)
							{
								foreach (var pair in _requestsById)
								{
									if (pair.Value.Item2 != u)
										continue;

									var subscrMsg = pair.Value.Item1;

									if (subscrMsg == null)
										continue;

									if (!subscrMsg.IsSubscribe)
										continue;

									var unsubscribeRequest = (ISubscriptionMessage)subscrMsg.Clone();
								
									unsubscribeRequest.IsSubscribe = false;

									// some messages can only be subscriptions
									if (unsubscribeRequest.IsSubscribe)
										continue;

									unsubscribeRequest.TransactionId = adapter.TransactionIdGenerator.GetNextId();
									unsubscribeRequest.OriginalTransactionId = subscrMsg.TransactionId;

									unsubscribes.Add((Message)unsubscribeRequest);
								}
							}

							foreach (var unsubscribe in unsubscribes)
							{
								adapter.SendInMessage(unsubscribe);
							}
						}

						adapter.SendInMessage(message);
					}

					break;
				}

				case MessageTypes.Portfolio:
				{
					ProcessPortfolioMessage((PortfolioMessage)message);
					break;
				}

				case MessageTypes.OrderRegister:
				case MessageTypes.OrderReplace:
				case MessageTypes.OrderCancel:
				case MessageTypes.OrderGroupCancel:
				{
					var ordMsg = (OrderMessage)message;
					ProcessPortfolioMessage(ordMsg.PortfolioName, ordMsg);
					break;
				}

				case MessageTypes.OrderPairReplace:
				{
					var ordMsg = (OrderPairReplaceMessage)message;
					ProcessPortfolioMessage(ordMsg.Message1.PortfolioName, ordMsg);
					break;
				}

				case MessageTypes.MarketData:
				{
					ProcessMarketDataRequest((MarketDataMessage)message);
					break;
				}

				case MessageTypes.ChangePassword:
				{
					var adapter = GetSortedAdapters().FirstOrDefault(a => a.IsMessageSupported(MessageTypes.ChangePassword));

					if (adapter == null)
						throw new InvalidOperationException(LocalizedStrings.Str629Params.Put(message.Type));

					adapter.SendInMessage(message);
					break;
				}

				default:
				{
					ProcessOtherMessage(message);
					break;
				}
			}
		}

		/// <inheritdoc />
		public event Action<Message> NewOutMessage;

		private void ProcessAdapterMessage(IMessageAdapter adapter, Message message)
		{
			if (message is ISubscriptionMessage subscrMsg)
				SendRequest((ISubscriptionMessage)subscrMsg.Clone(), adapter);
			else
				adapter.SendInMessage(message);
		}

		private void ProcessOtherMessage(Message message)
		{
			if (message.Adapter != null)
			{
				message.Adapter.SendInMessage(message);
				return;
			}

			if (message.Type.IsLookup())
			{
				var subscrMsg = (ISubscriptionMessage)message;

				IMessageAdapter[] adapters;

				if (subscrMsg.IsSubscribe)
				{
					adapters = GetAdapters(message, out var isPended, out _);

					if (isPended)
						return;

					if (adapters.Length == 0)
					{
						SendOutMessage(message.Type.ToResultType().CreateLookupResult(subscrMsg.TransactionId));
						return;
					}

					// historical request do not have unsubscribe operation
					if (subscrMsg.To == null)
						_keysToTransId[new SubscriptionKey(subscrMsg)] = subscrMsg.TransactionId;
				}
				else
					adapters = null;

				foreach (var pair in ToChild(subscrMsg, adapters))
					SendRequest(pair.Key, pair.Value);
			}
			else
			{
				var adapters = GetAdapters(message, out var isPended, out _);

				if (isPended)
					return;

				if (adapters.Length == 0)
					return;

				if (message is SubscriptionListRequestMessage listRequest)
					_subscriptionListRequests.Add(listRequest.TransactionId);

				adapters.ForEach(a => a.SendInMessage(message));
			}
		}

		private IMessageAdapter[] GetAdapters(Message message, out bool isPended, out bool skipSupportedMessages)
		{
			isPended = false;
			skipSupportedMessages = false;

			IMessageAdapter[] adapters = null;

			var adapter = message.Adapter;

			if (adapter != null)
				adapter = GetUnderlyingAdapter(adapter);

			if (adapter == null && message is MarketDataMessage mdMsg && mdMsg.DataType.IsSecurityRequired())
				adapter = _securityAdapters.TryGetValue(Tuple.Create(mdMsg.SecurityId, (MarketDataTypes?)mdMsg.DataType)) ?? _securityAdapters.TryGetValue(Tuple.Create(mdMsg.SecurityId, (MarketDataTypes?)null));

			if (adapter != null)
			{
				adapter = _activeAdapters.TryGetValue(adapter);

				if (adapter != null)
				{
					adapters = new[] { adapter };
					skipSupportedMessages = true;
				}
			}

			lock (_connectedResponseLock)
			{
				if (adapters == null)
					adapters = _messageTypeAdapters.TryGetValue(message.Type)?.Cache;

				if (adapters != null)
				{
					if (message.Type == MessageTypes.MarketData)
					{
						var mdMsg1 = (MarketDataMessage)message;
						var set = _nonSupportedAdapters.TryGetValue(mdMsg1.TransactionId);

						if (set != null)
						{
							adapters = adapters.Where(a => !set.Contains(GetUnderlyingAdapter(a))).ToArray();
						}
						else if (mdMsg1.DataType == MarketDataTypes.News && mdMsg1.SecurityId == default)
						{
							adapters = adapters.Where(a => !a.IsSecurityNewsOnly).ToArray();
						}

						if (adapters.Length == 0)
							adapters = null;
					}
					else if (message.Type == MessageTypes.SecurityLookup)
					{
						var isAll = ((SecurityLookupMessage)message).IsLookupAll();

						if (isAll)
							adapters = adapters.Where(a => a.IsSupportSecuritiesLookupAll).ToArray();
					}
				}

				if (adapters == null)
				{
					if (_pendingConnectAdapters.Count > 0)
					{
						isPended = true;
						_pendingMessages.Enqueue(message.Clone());
						return ArrayHelper.Empty<IMessageAdapter>();
					}
				}
			}

			if (adapters == null)
			{
				adapters = ArrayHelper.Empty<IMessageAdapter>();
			}

			if (adapters.Length == 0)
			{
				this.AddInfoLog(LocalizedStrings.Str629Params.Put(message));
				//throw new InvalidOperationException(LocalizedStrings.Str629Params.Put(message));
			}

			return adapters;
		}

		private IMessageAdapter[] GetSubscriptionAdapters(MarketDataMessage mdMsg)
		{
			var adapters = GetAdapters(mdMsg, out _, out var skipSupportedMessages).Where(a =>
			{
				if (skipSupportedMessages)
					return true;

				if (mdMsg.DataType != MarketDataTypes.CandleTimeFrame)
				{
					var isCandles = mdMsg.DataType.IsCandleDataType();

					if (a.IsMarketDataTypeSupported(mdMsg.DataType) && (!isCandles || a.IsCandlesSupported(mdMsg)))
						return true;
					else
					{
						switch (mdMsg.DataType)
						{
							case MarketDataTypes.Level1:
							case MarketDataTypes.OrderLog:
							case MarketDataTypes.News:
							case MarketDataTypes.Board:
								return false;
							case MarketDataTypes.MarketDepth:
							{
								if (mdMsg.BuildMode != MarketDataBuildModes.Load)
								{
									switch (mdMsg.BuildFrom)
									{
										case MarketDataTypes.Level1:
											return a.IsMarketDataTypeSupported(MarketDataTypes.Level1);
										case MarketDataTypes.OrderLog:
											return a.IsMarketDataTypeSupported(MarketDataTypes.OrderLog);
									}
								}

								return false;
							}
							case MarketDataTypes.Trades:
								return a.IsMarketDataTypeSupported(MarketDataTypes.OrderLog);
							default:
							{
								if (isCandles && a.TryGetCandlesBuildFrom(mdMsg, CandleBuilderProvider) != null)
									return true;

								return false;
								//throw new ArgumentOutOfRangeException(mdMsg.DataType.ToString());
							}
						}
					}
				}

				var original = mdMsg.GetTimeFrame();
				var timeFrames = a.GetTimeFrames(mdMsg.SecurityId, mdMsg.From, mdMsg.To).ToArray();

				if (timeFrames.Contains(original) || a.CheckTimeFrameByRequest)
					return true;

				if (mdMsg.AllowBuildFromSmallerTimeFrame)
				{
					var smaller = timeFrames
					              .FilterSmallerTimeFrames(original)
					              .OrderByDescending()
					              .FirstOr();

					if (smaller != null)
						return true;
				}

				return a.TryGetCandlesBuildFrom(mdMsg, CandleBuilderProvider) != null;
			}).ToArray();

			//if (!isPended && adapters.Length == 0)
			//	throw new InvalidOperationException(LocalizedStrings.Str629Params.Put(mdMsg));

			return adapters;
		}

		private IDictionary<ISubscriptionMessage, IMessageAdapter> ToChild(ISubscriptionMessage subscrMsg, IMessageAdapter[] adapters)
		{
			// sending to inner adapters unique child requests

			var child = new Dictionary<ISubscriptionMessage, IMessageAdapter>();

			if (subscrMsg.IsSubscribe)
			{
				foreach (var adapter in adapters)
				{
					var clone = (ISubscriptionMessage)subscrMsg.Clone();
					clone.TransactionId = adapter.TransactionIdGenerator.GetNextId();

					child.Add(clone, adapter);

					_parentChildMap.AddMapping(clone.TransactionId, subscrMsg.TransactionId, adapter);
				}
			}
			else
			{
				var originTransId = subscrMsg.OriginalTransactionId;

				if (originTransId == 0)
					originTransId = _keysToTransId.TryGetValue(new SubscriptionKey(subscrMsg));

				if (originTransId > 0)
				{
					foreach (var pair in _parentChildMap.GetChild(originTransId))
					{
						var adapter = pair.Value;

						var clone = (ISubscriptionMessage)subscrMsg.Clone();
						clone.TransactionId = adapter.TransactionIdGenerator.GetNextId();
						clone.OriginalTransactionId = pair.Key;

						child.Add(clone, adapter);

						_parentChildMap.AddMapping(clone.TransactionId, subscrMsg.TransactionId, adapter);
					}
				}
			}

			return child;
		}

		private void SendRequest(ISubscriptionMessage subscrMsg, IMessageAdapter adapter)
		{
			// if the message was looped back via IsBack=true
			_requestsById.TryAdd(subscrMsg.TransactionId, Tuple.Create(subscrMsg, GetUnderlyingAdapter(adapter)));
			adapter.SendInMessage((Message)subscrMsg);
		}

		private void ProcessMarketDataRequest(MarketDataMessage mdMsg)
		{
			switch (mdMsg.DataType)
			{
				case MarketDataTypes.News:
				case MarketDataTypes.Board:
				{
					IMessageAdapter[] adapters = null;

					if (mdMsg.IsSubscribe)
					{
						adapters = GetSubscriptionAdapters(mdMsg);

						if (adapters.Length == 0)
						{
							SendOutMarketDataNotSupported(mdMsg.TransactionId);
							break;
						}
					}
					
					foreach (var pair in ToChild(mdMsg, adapters))
						SendRequest(pair.Key, pair.Value);

					break;
				}

				default:
				{
					IMessageAdapter adapter;

					if (mdMsg.IsSubscribe)
					{
						adapter = GetSubscriptionAdapters(mdMsg).FirstOrDefault();

						if (adapter == null)
						{
							SendOutMarketDataNotSupported(mdMsg.TransactionId);
							break;
						}

						mdMsg = (MarketDataMessage)mdMsg.Clone();
					}
					else
					{
						var originTransId = mdMsg.OriginalTransactionId;

						if (originTransId == 0)
							originTransId = _keysToTransId.TryGetValue(new SubscriptionKey(mdMsg));

						if (!_requestsById.TryGetValue(originTransId, out var tuple))
						{
							this.AddInfoLog("Unsubscribe no found: {0}/{1}", originTransId, mdMsg);
							break;
						}
						
						adapter = tuple.Item2;

						// copy full subscription's details into unsubscribe request
						var transId = mdMsg.TransactionId;
						mdMsg = (MarketDataMessage)mdMsg.Clone();
						((MarketDataMessage)tuple.Item1)?.CopyTo(mdMsg);
						mdMsg.TransactionId = transId;
						mdMsg.OriginalTransactionId = originTransId;
					}

					SendRequest(mdMsg, adapter);
					break;
				}
			}
		}

		private void ProcessPortfolioMessage(string portfolioName, Message message)
		{
			var adapter = message.Adapter;

			if (adapter == null)
			{
				adapter = GetAdapter(portfolioName, message);

				if (adapter == null)
				{
					this.AddDebugLog("No adapter for {0}", message);
					return;
				}
			}
				
			adapter.SendInMessage(message);
		}

		private void ProcessPortfolioMessage(PortfolioMessage message)
		{
			if (message.IsSubscribe)
			{
				var adapter = GetAdapter(message.PortfolioName, message);

				if (adapter == null)
				{
					this.AddDebugLog("No adapter for {0}", message);

					SendOutMessage(new PortfolioMessage
					{
						OriginalTransactionId = message.TransactionId,
						Error = new InvalidOperationException(LocalizedStrings.Str629Params.Put(message))
					});
				}
				else
				{
					_portfolioAdapters.TryAdd(message.PortfolioName, GetUnderlyingAdapter(adapter));
					SendRequest((PortfolioMessage)message.Clone(), adapter);
				}
			}
			else
			{
				var originTransId = message.OriginalTransactionId;

				IMessageAdapter adapter;

				if (originTransId == 0)
					adapter = _portfolioAdapters.TryGetValue(message.PortfolioName);
				else if (_requestsById.TryGetValue(originTransId, out var tuple))
				{
					adapter = tuple.Item2;

					var transId = message.TransactionId;
					message = (PortfolioMessage)message.Clone();
					((PortfolioMessage)tuple.Item1).CopyTo(message);
					message.TransactionId = transId;
				}
				else
					adapter = null;

				if (adapter == null)
					this.AddDebugLog("No adapter for {0}", message);
				else
					SendRequest(message, adapter);
			}
		}

		private IMessageAdapter GetAdapter(string portfolioName, Message message)
		{
			if (portfolioName.IsEmpty())
				throw new ArgumentNullException(nameof(portfolioName));

			if (message == null)
				throw new ArgumentNullException(nameof(message));

			if (!_portfolioAdapters.TryGetValue(portfolioName, out var adapter))
			{
				return GetAdapters(message, out _, out _).FirstOrDefault();
			}
			else
			{
				var a = _activeAdapters.TryGetValue(adapter);

				return a ?? throw new InvalidOperationException(LocalizedStrings.Str1838Params.Put(adapter.GetType()));
			}
		}

		/// <summary>
		/// The embedded adapter event <see cref="IMessageChannel.NewOutMessage"/> handler.
		/// </summary>
		/// <param name="innerAdapter">The embedded adapter.</param>
		/// <param name="message">Message.</param>
		protected virtual void OnInnerAdapterNewOutMessage(IMessageAdapter innerAdapter, Message message)
		{
			List<Message> extra = null;

			if (!message.IsBack)
			{
				if (message.Adapter == null)
					message.Adapter = innerAdapter;

				switch (message.Type)
				{
					case MessageTypes.Connect:
						extra = new List<Message>();
						ProcessConnectMessage(innerAdapter, (ConnectMessage)message, extra);
						break;

					case MessageTypes.Disconnect:
						extra = new List<Message>();
						ProcessDisconnectMessage(innerAdapter, (DisconnectMessage)message, extra);
						break;

					case ExtendedMessageTypes.ReconnectingFinished:
						extra = new List<Message>();
						ProcessReconnectingFinished(innerAdapter, extra);
						break;

					case MessageTypes.MarketData:
						message = ProcessMarketDataResponse(innerAdapter, (MarketDataMessage)message);
						break;

					case MessageTypes.Portfolio:
					//case MessageTypes.PortfolioChange:
					case MessageTypes.PositionChange:
					{
						if (message.Type == MessageTypes.Portfolio)
						{
							var pfMsg1 = (PortfolioMessage)message;

							if (pfMsg1.Error != null)
							{
								_requestsById.Remove(pfMsg1.OriginalTransactionId);
								break;
							}
						}

						var pfMsg = (IPortfolioNameMessage)message;
						PortfolioAdapterProvider.SetAdapter(pfMsg.PortfolioName, GetUnderlyingAdapter(innerAdapter).Id);

						if (HasSubscription(DataType.PositionChanges) && innerAdapter.IsSupportSubscriptionByPortfolio())
							extra = new List<Message> { CreatePortfolioSubscription(innerAdapter, pfMsg.PortfolioName) };

						break;
					}

					case MessageTypes.Security:
						var secMsg = (SecurityMessage)message;
						SecurityAdapterProvider.SetAdapter(secMsg.SecurityId, null, GetUnderlyingAdapter(innerAdapter).Id);
						break;

					default:
						if (message.Type.IsLookupResult())
							message = ProcessLookupResult(message);

						break;
				}
			}

			if (message != null)
				SendOutMessage(message);

			if (extra != null)
			{
				foreach (var m in extra)
					SendOutMessage(m);	
			}
		}

		private void SendOutMarketDataNotSupported(long id)
		{
			SendOutMessage(new MarketDataMessage
			{
				OriginalTransactionId = id,
				IsNotSupported = true,
			});
		}

		private void SendOutError(Exception error)
		{
			SendOutMessage(error.ToErrorMessage());
		}

		private void SendOutMessage(Message message)
		{
			NewOutMessage?.Invoke(message);
		}

		private Message ProcessLookupResult(Message message)
		{
			var transId = ((IOriginalTransactionIdMessage)message).OriginalTransactionId;

			if (transId == 0)
				return message;

			var errorMsg = (IErrorMessage)message;

			if (errorMsg.Error != null)
				this.AddWarningLog("Lookup out: {0}", errorMsg);

			var parentId = _parentChildMap.ProcessChildResponse(transId, errorMsg.Error == null, out var allError);

			if (parentId == null)
				return null;

			var parentResponse = message.Type.CreateLookupResult(parentId.Value);

			if (allError)
				((IErrorMessage)parentResponse).Error = new InvalidOperationException(LocalizedStrings.Str629Params.Put(parentId));
				
			return parentResponse;
		}

		private static TMessage FillIdAndAdapter<TMessage>(IMessageAdapter adapter, TMessage m)
			where TMessage : Message, ISubscriptionMessage
		{
			m.TransactionId = adapter.TransactionIdGenerator.GetNextId();
			m.IsSubscribe = true;
			m.Adapter = adapter;
			m.IsBack = true;

			return m;
		}

		private PortfolioMessage CreatePortfolioSubscription(IMessageAdapter adapter, string portfolioName)
		{
			if (portfolioName.IsEmpty())
				throw new ArgumentNullException(nameof(portfolioName));

			var underlyingAdapter = GetUnderlyingAdapter(adapter);

			if (!_subscribedPortfolios.SafeAdd(underlyingAdapter, key => new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)).Add(portfolioName))
				return null;

			return FillIdAndAdapter(adapter, new PortfolioMessage { PortfolioName = portfolioName });
		}

		private void ProcessReconnectingFinished(IMessageAdapter innerAdapter, List<Message> extra)
		{
			if (!IsRestoreSubscriptionOnErrorReconnect)
				return;

			ProcessReSubscribe(innerAdapter, extra);
		}

		private void ProcessReSubscribe(IMessageAdapter innerAdapter, List<Message> extra)
		{
			var underlying = GetUnderlyingAdapter(innerAdapter);

			lock (_connectedResponseLock)
			{
				foreach (var pair in _nonSupportedAdapters.ToArray())
				{
					if (pair.Value.Remove(underlying) && pair.Value.Count == 0)
						_nonSupportedAdapters.Remove(pair.Key);
				}
			}

			lock (_requestsById.SyncRoot)
			{
				var adapterRequests = _requestsById.Where(p => p.Value.Item2 == underlying && p.Value.Item1 != null).ToArray();

				foreach (var pair in adapterRequests)
				{
					var transId = pair.Key;
					var subscrMsg = pair.Value.Item1;

					var tuple = _parentChildMap.RemoveByChild(transId);
					_requestsById.Remove(transId);

					if (subscrMsg.IsSubscribe)
					{
						subscrMsg = (ISubscriptionMessage)subscrMsg.Clone();
						subscrMsg.TransactionId = innerAdapter.TransactionIdGenerator.GetNextId();

						if (tuple != null)
							_parentChildMap.AddMapping(subscrMsg.TransactionId, tuple.First, underlying);

						var msg = (Message)subscrMsg;
						msg.Adapter = innerAdapter;
						msg.IsBack = true;
						extra.Add(msg);
					}
				}
			}
		}

		private void ProcessConnectMessage(IMessageAdapter innerAdapter, ConnectMessage message, List<Message> extra)
		{
			var underlyingAdapter = GetUnderlyingAdapter(innerAdapter);
			var wrapper = _activeAdapters[underlyingAdapter];

			var isError = message.Error != null;

			if (isError)
				this.AddErrorLog(LocalizedStrings.Str625Params, underlyingAdapter, message.Error);
			else
				this.AddInfoLog("Connected to '{0}'.", underlyingAdapter);

			Message[] notSupportedMsgs = null;

			lock (_connectedResponseLock)
			{
				_pendingConnectAdapters.Remove(underlyingAdapter);

				if (isError)
				{
					_connectedAdapters.Remove(wrapper);

					if (_pendingConnectAdapters.Count == 0)
					{
						notSupportedMsgs = _pendingMessages.ToArray();
						_pendingMessages.Clear();
					}
				}
				else
				{
					foreach (var supportedMessage in innerAdapter.SupportedMessages)
					{
						_messageTypeAdapters.SafeAdd(supportedMessage).Add(wrapper);
					}

					_connectedAdapters.Add(wrapper);

					extra.AddRange(_pendingMessages.Select(m =>
					{
						m.IsBack = true;
						m.Adapter = this;
						return m;
					}));

					_pendingMessages.Clear();
				}

				UpdateAdapterState(underlyingAdapter, message, extra);
			}

			if (notSupportedMsgs != null)
			{
				foreach (var notSupportedMsg in notSupportedMsgs)
				{
					SendOutError(new InvalidOperationException(LocalizedStrings.Str629Params.Put(notSupportedMsg.Type)));
				}
			}

			message.Adapter = underlyingAdapter;

			if (isError)
				return;

			bool AnySubscription()
			{
				lock (_requestsById.SyncRoot)
					return _requestsById.Any(p => p.Value.Item2 == underlyingAdapter);
			}

			if (AnySubscription())
			{
				if (IsRestoreSubscriptionOnNormalReconnect)
					ProcessReSubscribe(innerAdapter, extra);
			}
			else
			{
				if (HasSubscription(DataType.Transactions) && innerAdapter.IsOrderStatusRequired())
				{
					extra.Add(FillIdAndAdapter(innerAdapter, new OrderStatusMessage()));
				}

				if (HasSubscription(DataType.Securities) && innerAdapter.IsSecurityLookupRequired() && innerAdapter.IsSupportSecuritiesLookupAll)
				{
					extra.Add(FillIdAndAdapter(innerAdapter, new SecurityLookupMessage()));
				}

				if (HasSubscription(DataType.PositionChanges) && innerAdapter.IsPortfolioLookupRequired())
				{
					extra.Add(FillIdAndAdapter(innerAdapter, new PortfolioLookupMessage()));

					if (innerAdapter.IsSupportSubscriptionByPortfolio())
					{
						var portfolioNames = PortfolioAdapterProvider
						                     .Adapters
						                     .Where(p => p.Value == innerAdapter.Id)
						                     .Select(p => p.Key);

						foreach (var portfolioName in portfolioNames)
						{
							var msg = CreatePortfolioSubscription(innerAdapter, portfolioName);

							if (msg != null)
								extra.Add(msg);
						}
					}
				}
			}
		}

		private bool HasSubscription(DataType dataType) => _keysToTransId.SyncGet(c => c.Any(p => p.Key.Item1 == dataType && p.Key.Item2 == null));

		private void UpdateAdapterState(IMessageAdapter adapter, BaseConnectionMessage message, List<Message> extra)
		{
			var isConnect = message is ConnectMessage;
			var error = message.Error;

			if (isConnect)
			{
				void CreateConnectedMsg(ConnectionStates newState, Exception e = null)
				{
					extra.Add(new ConnectMessage { Error = e });
					_currState = newState;
				}

				if (error == null)
				{
					_adapterStates[adapter] = ConnectionStates.Connected;

					if (_currState == ConnectionStates.Connecting)
					{
						if (ConnectDisconnectEventOnFirstAdapter)
						{
							// raise Connected event only one time for the first adapter
							CreateConnectedMsg(ConnectionStates.Connected);
						}
						else
						{
							var noPending = _adapterStates.All(v => v.Value != ConnectionStates.Connecting);

							if (noPending)
								CreateConnectedMsg(ConnectionStates.Connected);
						}
					}
				}
				else
				{
					_adapterStates[adapter] = ConnectionStates.Failed;

					if (_currState == ConnectionStates.Connecting)
					{
						var allFailed = _adapterStates.All(v => v.Value == ConnectionStates.Failed);

						if (allFailed)
							CreateConnectedMsg(ConnectionStates.Failed, new InvalidOperationException(LocalizedStrings.Str2744));
					}
				}
			}
			else
			{
				if (error == null)
					_adapterStates[adapter] = ConnectionStates.Disconnected;
				else
					_adapterStates[adapter] = ConnectionStates.Failed;

				var noPending = _adapterStates.All(v => v.Value == ConnectionStates.Disconnected || v.Value == ConnectionStates.Failed);

				if (noPending)
				{
					extra.Add(new DisconnectMessage());
					_currState = ConnectionStates.Disconnected;
				}
			}
		}

		private void ProcessDisconnectMessage(IMessageAdapter innerAdapter, DisconnectMessage message, List<Message> extra)
		{
			var underlyingAdapter = GetUnderlyingAdapter(innerAdapter);
			var wrapper = _activeAdapters[underlyingAdapter];

			if (message.Error == null)
				this.AddInfoLog("Disconnected from '{0}'.", underlyingAdapter);
			else
				this.AddErrorLog(LocalizedStrings.Str627Params, underlyingAdapter, message.Error);

			lock (_connectedResponseLock)
			{
				foreach (var supportedMessage in innerAdapter.SupportedMessages)
				{
					var list = _messageTypeAdapters.TryGetValue(supportedMessage);

					if (list == null)
						continue;

					list.Remove(wrapper);

					if (list.Count == 0)
						_messageTypeAdapters.Remove(supportedMessage);
				}

				_connectedAdapters.Remove(wrapper);

				UpdateAdapterState(underlyingAdapter, message, extra);
			}

			message.Adapter = underlyingAdapter;
		}

		private MarketDataMessage ProcessMarketDataResponse(IMessageAdapter adapter, MarketDataMessage message)
		{
			var originalTransactionId = message.OriginalTransactionId;
			var tuple = _requestsById.TryGetValue(originalTransactionId);

			if (tuple == null)
			{
				if (_subscriptionListRequests.Contains(originalTransactionId))
					_requestsById.TryAdd(message.TransactionId, Tuple.Create((ISubscriptionMessage)null, GetUnderlyingAdapter(adapter)));

				return message;
			}

			if (!message.IsOk())
				_requestsById.Remove(originalTransactionId);

			MarketDataMessage CreateMarketDataMessage(IMessageAdapter a, long id, Exception error)
			{
				return new MarketDataMessage
				{
					OriginalTransactionId = id,
					Error = error,
					Adapter = a,
				};
			}

			var originMsg = (MarketDataMessage)tuple.Item1;

			if (_parentChildMap.IsChild(originalTransactionId))
			{
				if (!message.IsOk())
					this.AddWarningLog("Subscription out: {0}", message);

				var parentId = _parentChildMap.ProcessChildResponse(originalTransactionId, message.IsOk(), out var allError);

				if (parentId == null)
					return null;

				return CreateMarketDataMessage(null, parentId.Value, allError ? new InvalidOperationException(LocalizedStrings.Str629Params.Put(originMsg)) : null);
			}

			if (message.IsNotSupported)
			{
				lock (_connectedResponseLock)
				{
					// try loopback only subscribe messages
					if (originMsg.IsSubscribe)
					{
						var set = _nonSupportedAdapters.SafeAdd(originalTransactionId, k => new HashSet<IMessageAdapter>());
						set.Add(GetUnderlyingAdapter(adapter));

						originMsg.Adapter = this;
						originMsg.IsBack = true;
					}
				}

				return originMsg;
			}
			
			if (message.Error == null && originMsg.IsSubscribe && originMsg.To == null)
				_keysToTransId[new SubscriptionKey(originMsg)] = originalTransactionId;

			return CreateMarketDataMessage(adapter, originalTransactionId, message.Error);
		}

		private void SecurityAdapterProviderOnChanged(Tuple<SecurityId, MarketDataTypes?> key, Guid adapterId, bool changeType)
		{
			if (changeType)
			{
				var adapter = InnerAdapters.SyncGet(c => c.FindById(adapterId));

				if (adapter == null)
					_securityAdapters.Remove(key);
				else
					_securityAdapters[key] = adapter;
			}
			else
				_securityAdapters.Remove(key);
		}

		private void PortfolioAdapterProviderOnChanged(string key, Guid adapterId, bool changeType)
		{
			if (changeType)
			{
				var adapter = InnerAdapters.SyncGet(c => c.FindById(adapterId));

				if (adapter == null)
					_portfolioAdapters.Remove(key);
				else
					_portfolioAdapters[key] = adapter;
			}
			else
				_portfolioAdapters.Remove(key);
		}

		/// <inheritdoc />
		public override void Save(SettingsStorage storage)
		{
			lock (InnerAdapters.SyncRoot)
			{
				storage.SetValue(nameof(InnerAdapters), InnerAdapters.Select(a =>
				{
					var s = new SettingsStorage();

					s.SetValue("AdapterType", a.GetType().GetTypeName(false));
					s.SetValue("AdapterSettings", a.Save());
					s.SetValue("Priority", InnerAdapters[a]);

					return s;
				}).ToArray());
			}

			base.Save(storage);
		}

		/// <inheritdoc />
		public override void Load(SettingsStorage storage)
		{
			lock (InnerAdapters.SyncRoot)
			{
				InnerAdapters.Clear();

				var adapters = new Dictionary<Guid, IMessageAdapter>();

				foreach (var s in storage.GetValue<IEnumerable<SettingsStorage>>(nameof(InnerAdapters)))
				{
					try
					{
						var adapter = s.GetValue<Type>("AdapterType").CreateAdapter(_transactionIdGenerator);
						adapter.Load(s.GetValue<SettingsStorage>("AdapterSettings"));
						InnerAdapters[adapter] = s.GetValue<int>("Priority");

						adapters.Add(adapter.Id, adapter);
					}
					catch (Exception e)
					{
						e.LogError();
					}
				}

				_securityAdapters.Clear();

				foreach (var pair in SecurityAdapterProvider.Adapters)
				{
					if (!adapters.TryGetValue(pair.Value, out var adapter))
						continue;

					_securityAdapters.Add(pair.Key, adapter);
				}

				_portfolioAdapters.Clear();

				foreach (var pair in PortfolioAdapterProvider.Adapters)
				{
					if (!adapters.TryGetValue(pair.Value, out var adapter))
						continue;

					_portfolioAdapters.Add(pair.Key, adapter);
				}
			}

			base.Load(storage);
		}

		/// <summary>
		/// To release allocated resources.
		/// </summary>
		protected override void DisposeManaged()
		{
			SecurityAdapterProvider.Changed -= SecurityAdapterProviderOnChanged;
			PortfolioAdapterProvider.Changed -= PortfolioAdapterProviderOnChanged;

			ActiveAdapters.ForEach(a => a.Parent = null);

			base.DisposeManaged();
		}

		/// <summary>
		/// Create a copy of <see cref="BasketMessageAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public IMessageChannel Clone()
		{
			var clone = new BasketMessageAdapter(_transactionIdGenerator, SecurityAdapterProvider, PortfolioAdapterProvider, CandleBuilderProvider, StorageRegistry, SnapshotRegistry)
			{
				ExtendedInfoStorage = ExtendedInfoStorage,
				SupportCandlesCompression = SupportCandlesCompression,
				SuppressReconnectingErrors = SuppressReconnectingErrors,
				IsRestoreSubscriptionOnErrorReconnect = IsRestoreSubscriptionOnErrorReconnect,
				IsRestoreSubscriptionOnNormalReconnect = IsRestoreSubscriptionOnNormalReconnect,
				SupportBuildingFromOrderLog = SupportBuildingFromOrderLog,
				SupportOrderBookTruncate = SupportOrderBookTruncate,
				SupportOffline = SupportOffline,
				IgnoreExtraAdapters = IgnoreExtraAdapters,
				NativeIdStorage = NativeIdStorage,
				StorageDaysLoad = StorageDaysLoad,
				StorageMode = StorageMode,
				StorageFormat = StorageFormat,
				StorageDrive = StorageDrive,
				StorageFilterSubscription = StorageFilterSubscription,
				ConnectDisconnectEventOnFirstAdapter = ConnectDisconnectEventOnFirstAdapter,
				IsAutoUnSubscribeOnDisconnect = IsAutoUnSubscribeOnDisconnect,
				UseSeparatedChannels = UseSeparatedChannels,
			};

			clone.Load(this.Save());

			return clone;
		}

		object ICloneable.Clone()
		{
			return Clone();
		}

		bool IMessageChannel.IsOpened => true;

		void IMessageChannel.Open()
		{
		}

		void IMessageChannel.Close()
		{
		}

		event Action IMessageChannel.StateChanged
		{
			add { }
			remove { }
		}
	}
}