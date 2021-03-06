using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using StructureMap.Configuration.DSL;
using StructureMap.Pipeline;
using StructureMap.Pipeline.Lazy;
using StructureMap.TypeRules;
using StructureMap.Util;

namespace StructureMap.Graph
{
    /// <summary>
    ///   Models the runtime configuration of a StructureMap Container
    /// </summary>
    public class PluginGraph : IPluginGraph, IFamilyCollection, IDisposable
    {
        private readonly ConcurrentDictionary<Type, PluginFamily> _families = new ConcurrentDictionary<Type, PluginFamily>();
        private readonly IList<IFamilyPolicy> _policies = new List<IFamilyPolicy>();

        private readonly ConcurrentDictionary<Type, bool> _missingTypes = new ConcurrentDictionary<Type, bool>(); 

        private readonly List<Registry> _registries = new List<Registry>();
        private readonly LifecycleObjectCache _singletonCache = new LifecycleObjectCache();

        private readonly LightweightCache<string, PluginGraph> _profiles;

        public TransientTracking TransientTracking = TransientTracking.DefaultNotTrackedAtRoot;
        

        public string Name { get; set; }

        /// <summary>
        /// Specifies interception, construction selection, and setter usage policies
        /// </summary>
        public readonly Policies Policies;

        /// <summary>
        /// Holds a cache of concrete types that can be considered for closing generic interface
        /// types
        /// </summary>
        public readonly IList<Type> ConnectedConcretions = new List<Type>();

        /// <summary>
        /// Creates a top level PluginGraph with the default policies
        /// </summary>
        /// <param name="profile"></param>
        /// <returns></returns>
        public static PluginGraph CreateRoot(string profile = null)
        {
            var graph = new PluginGraph();
            graph.ProfileName = profile ?? "DEFAULT";

            graph.Families[typeof(Func<>)].SetDefault(new FuncFactoryTemplate());
            graph.Families[typeof(Func<,>)].SetDefault(new FuncWithArgFactoryTemplate());
            graph.Families[typeof(Lazy<>)].SetDefault(new LazyFactoryTemplate());

            return graph;
        }

        internal void addCloseGenericPolicyTo()
        {
            var policy = new CloseGenericFamilyPolicy(this);
            AddFamilyPolicy(policy);

            AddFamilyPolicy(new FuncBuildByNamePolicy());
            AddFamilyPolicy(new EnumerableFamilyPolicy());
        }


        internal PluginGraph NewChild()
        {
            return new PluginGraph
            {
                Parent = this
            };
        }

        
        internal PluginGraph ToNestedGraph()
        {
            return new PluginGraph
            {
                Parent = this,
                ProfileName = ProfileName + " - Nested"
            };
        }

        private PluginGraph()
        {
            Policies = new Policies(this);

            _profiles =
                new LightweightCache<string, PluginGraph>(name => new PluginGraph {ProfileName = name, Parent = this});

            ProfileName = "DEFAULT";

        }

        public PluginGraph Parent { get; private set; }

        /// <summary>
        /// The profile name of this PluginGraph or "DEFAULT" if it is the top 
        /// </summary>
        public string ProfileName { get; private set; }

        /// <summary>
        /// The cache for all singleton scoped objects
        /// </summary>
        public LifecycleObjectCache SingletonCache
        {
            get { return _singletonCache; }
        }

        /// <summary>
        /// Fetch the PluginGraph for the named profile.  Will
        /// create a new one on the fly for unrecognized names.
        /// Is case sensitive
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public PluginGraph Profile(string name)
        {
            return _profiles[name];
        }

        /// <summary>
        /// All the currently known profiles
        /// </summary>
        public IEnumerable<PluginGraph> Profiles
        {
            get { return _profiles.ToArray(); }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _families.Select(x => x.Value).ToArray().GetEnumerator();
        }

        IEnumerator<PluginFamily> IEnumerable<PluginFamily>.GetEnumerator()
        {
            return _families.Select(x => x.Value).ToArray().As<IEnumerable<PluginFamily>>().GetEnumerator();
        }

        PluginFamily IFamilyCollection.this[Type pluginType]
        {
            get
            {
                return _families.GetOrAdd(pluginType, type =>
                {
                    var family = _policies.FirstValue(x => x.Build(type)) ?? new PluginFamily(type);
                    family.Owner = this;

                    return family;
                });
            }
            set { _families[pluginType] = value; }
        }

        bool IFamilyCollection.Has(Type pluginType)
        {
            return _families.ContainsKey(pluginType);
        }



        /// <summary>
        /// Add a new family policy that can create new PluginFamily's on demand
        /// when there is no pre-existing family
        /// </summary>
        /// <param name="policy"></param>
        public void AddFamilyPolicy(IFamilyPolicy policy)
        {
            _policies.Insert(0, policy);
        }

        /// <summary>
        /// The list of Registry objects used to create this container
        /// </summary>
        internal List<Registry> Registries
        {
            get { return _registries; }
        }

        /// <summary>
        /// Access to all the known PluginFamily members
        /// </summary>
        public IFamilyCollection Families
        {
            get { return this; }
        }

        /// <summary>
        /// The top most PluginGraph.  If this is the root, will return itself.
        /// If a Profiled PluginGraph, returns its ultimate parent
        /// </summary>
        public PluginGraph Root
        {
            get { return Parent == null ? this : Parent.Root; }
        }

        internal bool IsRunningConfigure { get; set; }

        /// <summary>
        ///   Adds the concreteType as an Instance of the pluginType
        /// </summary>
        /// <param name = "pluginType"></param>
        /// <param name = "concreteType"></param>
        public virtual void AddType(Type pluginType, Type concreteType)
        {
            Families[pluginType].AddType(concreteType);
        }

        /// <summary>
        ///   Adds the concreteType as an Instance of the pluginType with a name
        /// </summary>
        /// <param name = "pluginType"></param>
        /// <param name = "concreteType"></param>
        /// <param name = "name"></param>
        public virtual void AddType(Type pluginType, Type concreteType, string name)
        {
            Families[pluginType].AddType(concreteType, name);
        }


        public readonly Queue<Registry> QueuedRegistries = new Queue<Registry>();

        /// <summary>
        /// Adds a Registry by type.  Requires that the Registry class have a no argument
        /// public constructor
        /// </summary>
        /// <param name="type"></param>
        public void ImportRegistry(Type type)
        {
            var all = Registries.Concat(QueuedRegistries);
            if (all.Any(x => x.GetType() == type)) return;

            try
            {
                var registry = (Registry) Activator.CreateInstance(type);
                QueuedRegistries.Enqueue(registry);
            }
            catch (Exception e)
            {
                throw new StructureMapException(
                    "Unable to create an instance for Registry type '{0}'.  Please check the inner exception for details"
                        .ToFormat(type.GetFullName()), e);
            }
        }

        public void ImportRegistry(Registry registry)
        {
            var all = Registries.Concat(QueuedRegistries).ToArray();

            if (Registry.RegistryExists(all, registry)) return;
 

            QueuedRegistries.Enqueue(registry);

        }

        public void AddFamily(PluginFamily family)
        {
            family.Owner = this;
            _families[family.PluginType] = family;
        }


        public bool HasInstance(Type pluginType, string name)
        {
            if (!HasFamily(pluginType))
            {
                return false;
            }

            return Families[pluginType].GetInstance(name) != null;
        }

        internal PluginFamily FindExistingOrCreateFamily(Type pluginType)
        {
            if (_families.ContainsKey(pluginType)) return _families[pluginType];

            var family = new PluginFamily(pluginType);
            _families[pluginType] = family;

            return family;
        }

        /// <summary>
        /// Does a PluginFamily already exist for the pluginType?  Will also test for open generic
        /// definition of a generic closed type
        /// </summary>
        /// <param name="pluginType"></param>
        /// <returns></returns>
        public bool HasFamily(Type pluginType)
        {
            if (_families.ContainsKey(pluginType)) return true;

            if (_missingTypes.ContainsKey(pluginType)) return false;


            if (_policies.Where(x => x.AppliesToHasFamilyChecks).ToArray().Any(x => x.Build(pluginType) != null))
            {
                return true;
            }

            _missingTypes.AddOrUpdate(pluginType, true, (type, b) => true);

            return false;
        }

        /// <summary>
        /// Can this PluginGraph resolve a default instance
        /// for the pluginType?
        /// </summary>
        /// <param name="pluginType"></param>
        /// <returns></returns>
        public bool HasDefaultForPluginType(Type pluginType)
        {
            if (!HasFamily(pluginType))
            {
                return false;
            }

            return Families[pluginType].GetDefaultInstance() != null;
        }

        /// <summary>
        /// Removes a PluginFamily from this PluginGraph
        /// and disposes that family and all of its Instance's
        /// </summary>
        /// <param name="pluginType"></param>
        public void EjectFamily(Type pluginType)
        {
            if (_families.ContainsKey(pluginType))
            {
                PluginFamily family = null;
                if (_families.TryRemove(pluginType, out family))
                {
                    family.SafeDispose();
                }
            }
        }

        internal void EachInstance(Action<Type, Instance> action)
        {
            _families.Each(family => family.Value.Instances.Each(i => action(family.Value.PluginType, i)));
        }

        /// <summary>
        /// Find a named instance for a given PluginType
        /// </summary>
        /// <param name="pluginType"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public Instance FindInstance(Type pluginType, string name)
        {
            if (!HasFamily(pluginType)) return null;


            return Families[pluginType].GetInstance(name);
        }

        /// <summary>
        /// Returns every instance in the PluginGraph for the pluginType
        /// </summary>
        /// <param name="pluginType"></param>
        /// <returns></returns>
        public IEnumerable<Instance> AllInstances(Type pluginType)
        {
            if (HasFamily(pluginType))
            {
                return Families[pluginType].Instances;
            }

            return Enumerable.Empty<Instance>();
        }

        void IDisposable.Dispose()
        {
            _families.Each(
                family =>
                {
                    family.Value.Instances.Each(instance =>
                    {
                        _singletonCache.Eject(family.Value.PluginType, instance);
                        if (instance is IDisposable)
                        {
                            instance.SafeDispose();
                        }
                    });
                });


            _profiles.Each(x => x.SafeDispose());
            _profiles.Clear();

            var containerFamily = _families[typeof (IContainer)];

            PluginFamily c;
            _families.TryRemove(typeof (IContainer), out c);
            containerFamily.RemoveAll();

            _missingTypes.Clear();

            _families.Each(x => x.SafeDispose());
            _families.Clear();
        }

        internal void ClearTypeMisses()
        {
            _missingTypes.Clear();
        }
    }

    public interface IFamilyCollection : IEnumerable<PluginFamily>
    {
        PluginFamily this[Type pluginType] { get; set; }
        bool Has(Type pluginType);
    }
}