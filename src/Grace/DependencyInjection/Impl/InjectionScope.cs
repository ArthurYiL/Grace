using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Grace.Data.Immutable;
using System.Threading;
using Grace.DependencyInjection.Impl.Wrappers;
using Grace.Diagnostics;
using Grace.Utilities;

namespace Grace.DependencyInjection.Impl
{
    /// <summary>
    /// Root injection scope that is inherited by the Dependency injection container
    /// </summary>
    [DebuggerDisplay("{DebugDisplayString,nq}")]
    [DebuggerTypeProxy(typeof(InjectionScopeDebuggerView))]
    public class InjectionScope : BaseExportLocatorScope, IInjectionScope
    {
        #region Fields
        private IActivationStrategyCollectionContainer<ICompiledWrapperStrategy> _wrappers;
        private ImmutableLinkedList<IInjectionValueProvider> _valueProviders = ImmutableLinkedList<IInjectionValueProvider>.Empty;
        private ImmutableLinkedList<IMissingExportStrategyProvider> _missingExportStrategyProviders =
            ImmutableLinkedList<IMissingExportStrategyProvider>.Empty;

        /// <summary>
        /// Activation strategy compiler
        /// </summary>
        protected IActivationStrategyCompiler ActivationStrategyCompiler;

        /// <summary>
        /// Provides IExportLocatorScope when requested
        /// </summary>
        protected ILifetimeScopeProvider LifetimeScopeProvider;

        /// <summary>
        /// Creates injection context when needed
        /// </summary>
        protected IInjectionContextCreator InjectionContextCreator;

        /// <summary>
        /// Implementation to tell if a type can be located
        /// </summary>
        protected ICanLocateTypeService CanLocateTypeService;

        /// <summary>
        /// Disposal scope providers, can be null
        /// </summary>
        protected IDisposalScopeProvider DisposalScopeProvider;

        /// <summary>
        /// Default disposal scope, null if DisposalScopeProvider is set
        /// </summary>
        protected IDisposalScope DisposalScope;

        /// <summary>
        /// string constant that is used to locate a lock for adding strategies to the container
        /// </summary>
        public const string ActivationStrategyAddLockName = "ActivationStrategyAddLock";
        #endregion

        #region Constructors

        /// <summary>
        /// Constructor that takes configuration action
        /// </summary>
        /// <param name="configuration">configuration action</param>
        public InjectionScope(Action<InjectionScopeConfiguration> configuration) : this(CreateConfiguration(configuration), null, "RootScope")
        {

        }

        /// <summary>
        /// Constructor takes a configuration object
        /// </summary>
        /// <param name="configuration"></param>
        public InjectionScope(IInjectionScopeConfiguration configuration) : this(configuration, null, "RootScope")
        {

        }

        /// <summary>
        /// Configuration object constructor
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="parent"></param>
        /// <param name="name"></param>
        public InjectionScope(IInjectionScopeConfiguration configuration, IInjectionScope parent, string name) :
            base(parent, name, new ImmutableHashTree<Type, ActivationStrategyDelegate>[configuration.CacheArraySize])
        {
            configuration.SetInjectionScope(this);

            ScopeConfiguration = configuration;

            InjectionContextCreator = configuration.Implementation.Locate<IInjectionContextCreator>();

            CanLocateTypeService = configuration.Implementation.Locate<ICanLocateTypeService>();

            ActivationStrategyCompiler = configuration.Implementation.Locate<IActivationStrategyCompiler>();

            StrategyCollectionContainer =
                configuration.Implementation.Locate<IActivationStrategyCollectionContainer<ICompiledExportStrategy>>();

            DecoratorCollectionContainer =
                configuration.Implementation.Locate<IActivationStrategyCollectionContainer<ICompiledDecoratorStrategy>>();

            for (var i = 0; i <= ArrayLengthMinusOne; i++)
            {
                ActivationDelegates[i] = ImmutableHashTree<Type, ActivationStrategyDelegate>.Empty;
            }

            if (configuration.AutoRegisterUnknown && Parent == null)
            {
                _missingExportStrategyProviders =
                    _missingExportStrategyProviders.Add(
                        configuration.Implementation.Locate<IMissingExportStrategyProvider>());
            }

            DisposalScopeProvider = configuration.DisposalScopeProvider;

            DisposalScope = DisposalScopeProvider == null ? this : null;
        }

        #endregion

        #region Public members

        /// <summary>
        /// Compiler that produces Activation Strategy Delegates
        /// </summary>
        IActivationStrategyCompiler IInjectionScope.StrategyCompiler => ActivationStrategyCompiler;

        /// <summary>
        /// Can Locator type
        /// </summary>
        /// <param name="type">type to locate</param>
        /// <param name="filter"></param>
        /// <param name="key">key to use while locating</param>
        /// <returns></returns>
        public bool CanLocate(Type type, ActivationStrategyFilter filter = null, object key = null)
        {
            return CanLocateTypeService.CanLocate(this, type, filter, key);
        }

        /// <summary>
        /// Locate a specific type
        /// </summary>
        /// <param name="type">type to locate</param>
        /// <returns>located instance</returns>
        public object Locate(Type type)
        {
            var hashCode = type.GetHashCode();

            var func = ActivationDelegates[hashCode & ArrayLengthMinusOne].GetValueOrDefault(type, hashCode);

            return func != null ? func(this, DisposalScope ?? DisposalScopeProvider.ProvideDisposalScope(this), null) : LocateObjectFactory(this, DisposalScope ?? DisposalScopeProvider.ProvideDisposalScope(this), type, null, null, null, false, false);
        }

        /// <summary>
        /// Locate type
        /// </summary>
        /// <typeparam name="T">type to locate</typeparam>
        /// <returns>located instance</returns>
        public T Locate<T>()
        {
            return (T)Locate(typeof(T));
        }

        /// <summary>
        /// Locate specific type using extra data or key
        /// </summary>
        /// <param name="type">type to locate</param>
        /// <param name="extraData">extra data to be used during construction</param>
        /// <param name="consider">filter out exports you don't want to consider</param>
        /// <param name="withKey">key to use for locating type</param>
        /// <param name="isDynamic">skip cache and look through exports</param>
        /// <returns>located instance</returns>
        // ReSharper disable once MethodOverloadWithOptionalParameter
        public object Locate(Type type, object extraData = null, ActivationStrategyFilter consider = null, object withKey = null, bool isDynamic = false)
        {
            var context = CreateInjectionContextFromExtraData(type, extraData);

            if (withKey == null && consider == null && !isDynamic)
            {
                var hash = type.GetHashCode();

                var func = ActivationDelegates[hash & ArrayLengthMinusOne].GetValueOrDefault(type, hash);

                if (func != null)
                {
                    return func(this, DisposalScope ?? DisposalScopeProvider.ProvideDisposalScope(this), context);
                }
            }

            return LocateObjectFactory(this, DisposalScope ?? DisposalScopeProvider.ProvideDisposalScope(this), type, consider, withKey, context, false, isDynamic);
        }

        /// <summary>
        /// Locate specific type using extra data or key
        /// </summary>
        /// <typeparam name="T">type to locate</typeparam>
        /// <param name="extraData">extra data</param>
        /// <param name="consider">filter out exports you don't want to consider</param>
        /// <param name="withKey">key to use during construction</param>
        /// <param name="isDynamic">skip cache and look at all strategies</param>
        /// <returns>located instance</returns>
        // ReSharper disable once MethodOverloadWithOptionalParameter
        public T Locate<T>(object extraData = null, ActivationStrategyFilter consider = null, object withKey = null, bool isDynamic = false)
        {
            return (T)Locate(typeof(T), extraData, consider, withKey, isDynamic);
        }

        /// <summary>
        /// Locate all instances of a type
        /// </summary>
        /// <param name="type">type to locate</param>
        /// <param name="extraData">extra data </param>
        /// <param name="consider">provide method to filter out exports</param>
        /// <param name="withKey">key to use while locating</param>
        /// <param name="comparer">comparer to use for sorting</param>
        /// <returns>list of all type</returns>
        public List<object> LocateAll(Type type, object extraData = null, ActivationStrategyFilter consider = null, object withKey = null, IComparer<object> comparer = null)
        {
            return InternalLocateAll(type, extraData, consider, withKey, comparer);
        }

        /// <summary>
        /// Locate all of a specific type
        /// </summary>
        /// <typeparam name="T">type to locate</typeparam>
        /// <param name="extraData">extra data to use during construction</param>
        /// <param name="consider">provide method to filter out exports</param>
        /// <param name="withKey">key to use while locating</param>
        /// <param name="comparer">comparer to use for sorting</param>
        /// <returns>list of all located</returns>
        public List<T> LocateAll<T>(object extraData = null, ActivationStrategyFilter consider = null, object withKey = null, IComparer<T> comparer = null)
        {
            return InternalLocateAll(typeof(T), extraData, consider, withKey, comparer);
        }

        /// <summary>
        /// Try to locate a specific type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value">located value</param>
        /// <param name="extraData">extra data to be used during construction</param>
        /// <param name="consider">filter out exports you don't want</param>
        /// <param name="withKey">key to use while locating</param>
        /// <param name="isDynamic">skip cache and look at all exports</param>
        /// <returns></returns>
        public bool TryLocate<T>(out T value, object extraData = null, ActivationStrategyFilter consider = null, object withKey = null, bool isDynamic = false)
        {
            IInjectionContext context = CreateInjectionContextFromExtraData(typeof(T), extraData);

            var newValue = LocateObjectFactory(this, this, typeof(T), consider, withKey, context, true, isDynamic);

            bool returnValue = false;

            if (newValue != null)
            {
                returnValue = true;
                value = (T)newValue;
            }
            else
            {
                value = default(T);
            }

            return returnValue;
        }

        /// <summary>
        /// Try to locate an export by type
        /// </summary>
        /// <param name="type">locate type</param>
        /// <param name="value">out value</param>
        /// <param name="extraData">extra data to use during locate</param>
        /// <param name="consider">filter out exports you don't want</param>
        /// <param name="withKey">key to use during locate</param>
        /// <param name="isDynamic">skip cache and look at all exports</param>
        /// <returns>returns tue if export found</returns>
        public bool TryLocate(Type type, out object value, object extraData = null, ActivationStrategyFilter consider = null, object withKey = null, bool isDynamic = false)
        {
            IInjectionContext context = CreateInjectionContextFromExtraData(type, extraData);

            value = LocateObjectFactory(this, this, type, consider, withKey, context, true, isDynamic);

            return value != null;
        }

        /// <summary>
        /// Create as a new IExportLocate scope
        /// </summary>
        /// <param name="scopeName">scope name</param>
        /// <returns>new scope</returns>
        public IExportLocatorScope BeginLifetimeScope(string scopeName = "")
        {
            return LifetimeScopeProvider == null
                ? new LifetimeScope(this, scopeName, ActivationDelegates)
                : LifetimeScopeProvider.CreateScope(this, scopeName, ActivationDelegates);
        }

        /// <summary>
        /// Configure the injection scope
        /// </summary>
        /// <param name="registrationBlock"></param>
        public void Configure(Action<IExportRegistrationBlock> registrationBlock)
        {
            lock (GetLockObject(ActivationStrategyAddLockName))
            {
                var provider = ScopeConfiguration.Implementation.Locate<IExportRegistrationBlockValueProvider>();

                registrationBlock(provider);

                foreach (var inspector in provider.GetInspectors())
                {
                    StrategyCollectionContainer.AddInspector(inspector);
                    WrapperCollectionContainer.AddInspector(inspector);
                    DecoratorCollectionContainer.AddInspector(inspector);
                }

                foreach (var missingExportStrategyProvider in provider.GetMissingExportStrategyProviders())
                {
                    _missingExportStrategyProviders = _missingExportStrategyProviders.Add(missingExportStrategyProvider);
                }

                foreach (var injectionValueProvider in provider.GetValueProviders())
                {
                    _valueProviders = _valueProviders.Add(injectionValueProvider);
                }

                foreach (var compiledWrapperStrategy in provider.GetWrapperStrategies())
                {
                    WrapperCollectionContainer.AddStrategy(compiledWrapperStrategy);
                }

                foreach (var decorator in provider.GetDecoratorStrategies())
                {
                    DecoratorCollectionContainer.AddStrategy(decorator);
                }

                foreach (var strategy in provider.GetExportStrategies())
                {
                    StrategyCollectionContainer.AddStrategy(strategy);

                    foreach (var secondaryStrategy in strategy.SecondaryStrategies())
                    {
                        StrategyCollectionContainer.AddStrategy(secondaryStrategy);
                    }
                }
            }
        }

        /// <summary>
        /// Scope configuration
        /// </summary>
        public IInjectionScopeConfiguration ScopeConfiguration { get; }

        /// <summary>
        /// Strategies associated with this scope
        /// </summary>
        public IActivationStrategyCollectionContainer<ICompiledExportStrategy> StrategyCollectionContainer { get; }

        /// <summary>
        /// Wrappers associated with this scope
        /// </summary>
        public IActivationStrategyCollectionContainer<ICompiledWrapperStrategy> WrapperCollectionContainer => _wrappers ?? GetWrappers();

        /// <summary>
        /// Decorators associated with this scope
        /// </summary>
        public IActivationStrategyCollectionContainer<ICompiledDecoratorStrategy> DecoratorCollectionContainer { get; }

        /// <summary>
        /// List of missing export strategy providers
        /// </summary>
        public IEnumerable<IMissingExportStrategyProvider> MissingExportStrategyProviders => _missingExportStrategyProviders;

        /// <summary>
        /// List of value providers that can be used during construction of linq expression
        /// </summary>
        public IEnumerable<IInjectionValueProvider> InjectionValueProviders => _valueProviders;

        /// <summary>
        /// Locate an export from a child scope
        /// </summary>
        /// <param name="childScope">scope where the locate originated</param>
        /// <param name="disposalScope"></param>
        /// <param name="type">type to locate</param>
        /// <param name="extraData"></param>
        /// <param name="consider"></param>
        /// <param name="key"></param>
        /// <param name="allowNull"></param>
        /// <param name="isDynamic"></param>
        /// <returns>configuration object</returns>
        object IInjectionScope.LocateFromChildScope(IExportLocatorScope childScope, IDisposalScope disposalScope, Type type, object extraData, ActivationStrategyFilter consider, object key, bool allowNull, bool isDynamic)
        {
            return LocateObjectFactory(childScope, disposalScope, type, consider, key, CreateInjectionContextFromExtraData(type, extraData), allowNull, false);
        }
        
        /// <summary>
        /// Creates a new child scope
        /// This is best used for long term usage, not per request scenario
        /// </summary>
        /// <param name="configure">configure scope</param>
        /// <param name="scopeName">scope name </param>
        /// <returns></returns>
        public IInjectionScope CreateChildScope(Action<IExportRegistrationBlock> configure = null, string scopeName = "")
        {
            var newScope = new InjectionScope(ScopeConfiguration, this, scopeName);

            if (configure != null)
            {
                newScope.Configure(configure);
            }

            return newScope;
        }

        #endregion

        #region Non public members

        /// <summary>
        /// Creates a new configuration object
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns></returns>
        private static IInjectionScopeConfiguration CreateConfiguration(Action<InjectionScopeConfiguration> configuration)
        {
            var configurationObject = new InjectionScopeConfiguration();

            configuration?.Invoke(configurationObject);

            return configurationObject;
        }

        /// <summary>
        /// Create an injection context from extra data
        /// </summary>
        /// <param name="type"></param>
        /// <param name="extraData"></param>
        /// <returns></returns>
        protected virtual IInjectionContext CreateInjectionContextFromExtraData(Type type, object extraData)
        {
            return InjectionContextCreator.CreateContext(type, extraData);
        }

        private object LocateObjectFactory(IExportLocatorScope scope, IDisposalScope disposalScope, Type type, ActivationStrategyFilter consider, object key, IInjectionContext injectionContext, bool allowNull, bool isDynamic)
        {
            if (isDynamic)
            {
                if (type.IsArray)
                {
                    return DynamicArray(scope, disposalScope, type,consider, key, injectionContext, allowNull);
                }

                if (type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return DynamicIEnumerable(scope, disposalScope, type,consider, key, injectionContext, allowNull);
                }
            }

            var compiledDelegate = ActivationStrategyCompiler.FindDelegate(this, type, consider, key);

            if (compiledDelegate != null)
            {
                if (key == null && consider == null)
                {
                    compiledDelegate = AddObjectFactory(type, compiledDelegate);
                }

                return compiledDelegate(scope, disposalScope ?? (DisposalScope ?? DisposalScopeProvider.ProvideDisposalScope(scope)), injectionContext);
            }

            if (Parent != null)
            {
                var injectionScopeParent = (IInjectionScope)Parent;

                return injectionScopeParent.LocateFromChildScope(this, disposalScope, type, injectionContext, consider, key, allowNull, isDynamic);
            }

            if (!allowNull)
            {
                throw new Exception("Could not locate type: " + type.FullName);
            }

            return null;
        }

        private object DynamicIEnumerable(IExportLocatorScope scope, IDisposalScope disposalScope, Type type, ActivationStrategyFilter consider, object key, IInjectionContext injectionContext, bool allowNull)
        {
            throw new NotImplementedException();
        }

        private object DynamicArray(IExportLocatorScope scope, IDisposalScope disposalScope, Type type, ActivationStrategyFilter consider, object key, IInjectionContext injectionContext, bool allowNull)
        {
            throw new NotImplementedException();
        }

        private ActivationStrategyDelegate AddObjectFactory(Type type, ActivationStrategyDelegate activationStrategyDelegate)
        {
            var hashCode = type.GetHashCode();

            return ImmutableHashTree.ThreadSafeAdd(ref ActivationDelegates[hashCode & ArrayLengthMinusOne],
                                                   type,
                                                   activationStrategyDelegate);
        }

        private IActivationStrategyCollectionContainer<ICompiledWrapperStrategy> GetWrappers()
        {
            if (_wrappers != null)
            {
                return _wrappers;
            }

            var wrapperCollectionProvider = ScopeConfiguration.Implementation.Locate<IDefaultWrapperCollectionProvider>();

            Interlocked.CompareExchange(ref _wrappers, wrapperCollectionProvider.ProvideCollection(this), null);

            return _wrappers;
        }

        private List<T> InternalLocateAll<T>(Type type, object extraData, ActivationStrategyFilter filter, object withKey, IComparer<T> comparer)
        {
            List<T> returnList = new List<T>();

            LocateEnumerablesFromStrategyCollection(type, extraData, filter, returnList);

            if (type.IsConstructedGenericType)
            {
                var genericType = type.GetGenericTypeDefinition();

                LocateEnumerablesFromStrategyCollection(genericType, extraData, filter, returnList);
            }

            if (comparer != null)
            {
                returnList.Sort(comparer);
            }

            if (Parent != null)
            {
                var parentValues = Parent.LocateAll()
            }

            return returnList;
        }

        private void LocateEnumerablesFromStrategyCollection<T>(Type type, object extraData, ActivationStrategyFilter filter,
            List<T> returnList)
        {
            var collection = StrategyCollectionContainer.GetActivationStrategyCollection(type);

            if (collection != null)
            {
                foreach (var strategy in collection.GetStrategies())
                {
                    if (strategy.HasConditions)
                    {
                        bool pass = true;

                        foreach (var condition in strategy.Conditions)
                        {
                            if (!condition.MeetsCondition(strategy, new StaticInjectionContext(type)))
                            {
                                pass = false;
                                break;
                            }
                        }

                        if (!pass)
                        {
                            continue;
                        }
                    }

                    if (filter != null && !filter(strategy))
                    {
                        continue;
                    }

                    var activationDelegate = strategy.GetActivationStrategyDelegate(this, ActivationStrategyCompiler, type);

                    if (activationDelegate != null)
                    {
                        returnList.Add(
                            (T)activationDelegate(this, DisposalScope ?? DisposalScopeProvider.ProvideDisposalScope(this),
                                extraData != null ? CreateInjectionContextFromExtraData(type, extraData) : null));
                    }
                }
            }
        }

        private string DebugDisplayString => "Exports: " + StrategyCollectionContainer.GetAllStrategies().Count();

        #endregion

    }
}
