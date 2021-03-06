using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SimpleMapper{
    public static class MapperExtensions{
        internal static ObjectMapper Mapper = new ObjectMapper();

        public static IEnumerable<TDestination> MapTo<TSource, TDestination>(this IEnumerable<TSource> source)
            where TDestination : class where TSource : class{
            return Mapper.MapMany<TSource, TDestination>(source);
        }

        public static IEnumerable<TDestination> MapTo<TDestination>(this IEnumerable source) where TDestination : class{
            return (from object item in source select item.MapTo<TDestination>());
        }

        public static IEnumerable<TDestination> MapToAs<TDestination, TMapAs>(this IEnumerable source)
            where TDestination : class where TMapAs : class{
            return (from object item in source select item.MapToAs<TDestination, TMapAs>());
        }

        public static TDestination MapToAs<TDestination, TMapAs>(this object source) where TDestination : class{
            return Mapper.CreateDestinationObjectAndMap<TDestination>(source, typeof (TMapAs));
        }

        public static TDestination MapTo<TDestination>(this object source, TDestination to) where TDestination : class{
            if (source == null) return null;
            Mapper.Map(source, to);
            return to;
        }

        public static TDestination MapTo<TDestination>(this object source) where TDestination : class{
            return Mapper.CreateDestinationObjectAndMap<TDestination>(source, typeof (TDestination));
        }

        public static void MapFrom(this object source, params object[] items){
            foreach (var item in items){
                Mapper.Map(item, source);
            }
        }
    }

    public interface IMapperConfiguration{
        Func<Type, object> DefaultActivator { get; }
        bool CreateMissingMapsAutomaticly { get; }
        IDictionary<KeyValuePair<Type, Type>, ITypeConverter> Conversions { get; }
        IList<Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>>> Conventions { get; }
        IDictionary<KeyValuePair<Type, Type>, IPropertyMap> Maps { get; }

        void AddMap<TSource, TDestination>(Action<TSource, TDestination> map, bool useConventionMapping)
            where TSource : class where TDestination : class;

        void AddMap(Type source, Type destination, IPropertyMap container);
        void AddConvention(Func<PropertyInfo[], PropertyInfo[], IEnumerable<object>> convention);
        void AddConversion<TSource, TDestination>(Func<TSource, TDestination> conversion);
        IMapperConfiguration Initialize();
    }

    public class MapperConfiguration : IMapperConfiguration{
        public Func<Type, object> DefaultActivator { get; set; }
        public bool CreateMissingMapsAutomaticly { get; set; }
        public IDictionary<KeyValuePair<Type, Type>, ITypeConverter> Conversions { get; private set; }
        public IList<Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>>> Conventions { get; private set; }
        public IDictionary<KeyValuePair<Type, Type>, IPropertyMap> Maps { get; private set; }

        internal MapperConfigLoader ConfigLoader { get; private set; }

        public MapperConfiguration(){
            Maps = new Dictionary<KeyValuePair<Type, Type>, IPropertyMap>();
            ConfigLoader = new MapperConfigLoader();
            Conventions = new List<Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>>>{ ObjectMapper.SameNameConventionIgnoreCase };
            Conversions = new Dictionary<KeyValuePair<Type, Type>, ITypeConverter>();
            AddConversion(ObjectMapper.DateToStringConversion);
            AddConversion(ObjectMapper.StringToDateConversion);
            AddConversion(ObjectMapper.IntToStringConversion);
            AddConversion(ObjectMapper.StringToIntConversion);
            CreateMissingMapsAutomaticly = true;
            DefaultActivator = Activator.CreateInstance;
        }

        public IMapperConfiguration Initialize(){
            ConfigLoader.ActivateMappers();
            foreach (var map in Maps.Values){
                map.Initialize();
            }
            return this;
        }

        public void AddMap<TSource, TDestination>(Action<TSource, TDestination> map, bool useConventionMapping)
            where TSource : class where TDestination : class{
            AddMap(typeof (TSource), typeof (TDestination), new ManualMap<TSource, TDestination>(map, useConventionMapping, this));
        }

        public void AddMap(Type source, Type destination, IPropertyMap container){
            Maps.Add(new KeyValuePair<Type, Type>(source, destination), container);
        }

        public void AddConvention(Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>> convention){
            Conventions.Add(convention);
        }

        public void AddConversion<TSource, TDestination>(Func<TSource, TDestination> conversion){
            Conversions[new KeyValuePair<Type, Type>(typeof (TSource), typeof (TDestination))] = new TypeConversionContainer<TSource, TDestination>(conversion);
        }
    }

    public class ObjectMapper{
        private static IMapperConfiguration _currentConfiguration;

        internal static IMapperConfiguration CurrentConfiguration{
            get { return _currentConfiguration ?? (_currentConfiguration = new MapperConfiguration().Initialize()); }
        }

        public static void Configure(IMapperConfiguration configuration){
            _currentConfiguration = configuration.Initialize();
        }

        public IMapperConfiguration Configuration { get; set; }

        public static Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>> SameNameConventionIgnoreCase =
            (s, d) => from destination in d
                join source in s on destination.Name.ToLower() equals source.Name.ToLower()
                where source.CanRead && destination.CanWrite
                select new{source, destination};

        public static readonly Func<DateTime, string> DateToStringConversion =
            time => time.ToString(CultureInfo.CurrentUICulture);

        public static readonly Func<string, DateTime> StringToDateConversion = s => DateTime.Parse(s);
        public static readonly Func<int, string> IntToStringConversion = i => i.ToString(CultureInfo.CurrentCulture);
        public static readonly Func<string, int> StringToIntConversion = s => Int32.Parse(s);

        internal ObjectMapper(){
            Configuration = CurrentConfiguration;
        }

        internal ObjectMapper(IMapperConfiguration configuration){
            Configuration = configuration;
        }

        public IEnumerable<TDestination> MapMany<TSource, TDestination>(IEnumerable<TSource> source,
            Type lookupType = null) where TDestination : class where TSource : class{
            var items = new List<TDestination>();
            if (source == null) return items;

            var map = GetMap(typeof (TSource), lookupType ?? typeof (TDestination));

            foreach (var item in source){
                var destination = CreateDestinationObject<TDestination>(item, map);
                map.Map(item, destination);
                items.Add(destination);
            }

            return items;
        }

        internal T CreateDestinationObject<T>(object source, IPropertyMap map = null) where T : class{
            if (map == null) map = GetMap(source.GetType(), typeof (T));

            var destination = map.CreateDestinationObject(source);

            return (T) destination ?? (T) Configuration.DefaultActivator(typeof (T));
        }

        internal void CreateMap(Type source, Type destination){
            var type = typeof (ConventionMap<,>).MakeGenericType(source, destination);
            var map = Configuration.DefaultActivator(type);

            Configuration.Maps.Add(new KeyValuePair<Type, Type>(source, destination), (IPropertyMap) type.GetMethod("CreateMap").Invoke(map, null));
        }

        internal void CreateMap<TSource, TDestination>(){
            Configuration.Maps.Add(new KeyValuePair<Type, Type>(typeof (TSource), typeof (TDestination)), new ConventionMap<TSource, TDestination>(Configuration));
        }

        internal void Map(object source, object destination, IPropertyMap map = null){
            if (source == null) return;
            if (destination == null) throw new ApplicationException("Destination object must not be null");
            if (map == null) map = GetMap(source.GetType(), destination.GetType());

            map.Map(source, destination);
        }

        internal IPropertyMap GetMap(Type sourceType, Type destinationType){
            var key = new KeyValuePair<Type, Type>(sourceType, destinationType);

            if (Configuration.Maps.ContainsKey(key)) return Configuration.Maps[key];

            if (Configuration.CreateMissingMapsAutomaticly) CreateMap(sourceType, destinationType);
            else throw new MapperException(string.Format("No map configured to map from {0} to {1}", sourceType.Name, destinationType.Name));

            return Configuration.Maps[key];
        }

        internal TDestination CreateDestinationObjectAndMap<TDestination>(object source, Type mapAs)
            where TDestination : class{
            if (source == null) return null;
            var map = GetMap(source.GetType(), mapAs);
            var destination = CreateDestinationObject<TDestination>(source, map);
            Map(source, destination, map);
            return destination;
        }
    }

    public interface IPropertyMap{
        void Map(object source, object destination);
        object CreateDestinationObject(object source);
        void Initialize();
    }

    public class ConventionMap<TSource, TDestination> : IPropertyMap{
        private IEnumerable<PropertyLookup> _map;
        private readonly IMapperConfiguration _configuration;

        public ConventionMap(IMapperConfiguration configuration){
            _configuration = configuration;
            IgnoreProperties = new List<string>();
            Conventions = new List<Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>>>();
            Conversions = new Dictionary<KeyValuePair<Type, Type>, ITypeConverter>();
        }

        public List<string> IgnoreProperties { get; set; }
        public Func<TSource, TDestination> CustomActivator { get; set; }
        public List<Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>>> Conventions { get; set; }
        public Dictionary<KeyValuePair<Type, Type>, ITypeConverter> Conversions { get; set; }

        public virtual void Initialize(){
            var conventionMap = new List<dynamic>();
            var sourceProperties = typeof (TSource).GetProperties();
            var destinationProperties = typeof (TDestination).GetProperties();
            var propertyMap = new List<PropertyLookup>();

            if (!Conventions.Any()) throw new ApplicationException("No conventions configured!");

            Conventions.ForEach(convention => conventionMap.AddRange(convention(sourceProperties, destinationProperties)));

            conventionMap.ForEach(map =>{
                                      if (!map.source.CanRead) throw new MapperException(string.Format("Property to read from {0} has no getter!", map.source.Name));
                                      if (!map.destination.CanWrite) throw new MapperException(string.Format("Property to write to {0} has no setter", map.destination.Name));

                                      var item = new PropertyLookup{Source = map.source, Destination = map.destination};

                                      if (map.source.PropertyType.Name != map.destination.PropertyType.Name){
                                          var conversionKey = new KeyValuePair<Type, Type>(map.source.PropertyType, map.destination.PropertyType);
                                          if (!Conversions.ContainsKey(conversionKey)) throw new MapperException("Matched properties are not of same type, and no conversion available!");
                                          item.Conversion = _configuration.Conversions[conversionKey];
                                      }

                                      propertyMap.Add(item);
                                  });

            propertyMap = propertyMap.Distinct().ToList();
            propertyMap.RemoveAll(x => IgnoreProperties.Contains(x.Destination.PropertyType.Name));

            _map = propertyMap;
        }

        public virtual void Map(object source, object destination){
            foreach (var lookup in _map){
                try{
                    var fromValue = lookup.Source.GetValue(source, null);

                    if (lookup.Conversion != null){
                        fromValue = lookup.Conversion.Convert(fromValue);
                    }

                    lookup.Destination.SetValue(destination, fromValue, null);
                }
                catch (Exception ex){
                    throw new MapperException("There was an error setting mapped property value", ex);
                }
            }
        }

        public object CreateDestinationObject(object source){
            return CustomActivator == null ? (object) null : CustomActivator((TSource) source);
        }
    }

    public class ManualMap<TSource, TDestination> : ConventionMap<TSource, TDestination>{
        public Action<TSource, TDestination> ObjectMap { get; set; }
        public bool UseConventionMapping { get; internal set; }

        public ManualMap(Action<TSource, TDestination> map, bool useConventionMapping, IMapperConfiguration configuration) : base(configuration){            
            ObjectMap = map;
            UseConventionMapping = useConventionMapping;
        }

        public override void Initialize(){
            if (!UseConventionMapping) return;
            base.Initialize();
        }

        public override void Map(object source, object destination){
            try{
                if (UseConventionMapping){
                    base.Map(source, destination);
                }

                ObjectMap((TSource) source, (TDestination) destination);
            }
            catch (Exception ex){
                throw new MapperException( string.Format("There was an error applying manual property map from {0} to {1} ", typeof (TSource).Name, typeof (TDestination).Name), ex);
            }
        }
    }

    public interface ITypeConverter{
        object Convert(object source);
    }

    public class TypeConversionContainer<TSource, TDestination> : ITypeConverter{
        private readonly Func<TSource, TDestination> _conversion;

        public TypeConversionContainer(Func<TSource, TDestination> conversion){
            _conversion = conversion;
        }

        public object Convert(object source){
            try{
                return _conversion((TSource) source);
            }
            catch (Exception ex){
                throw new MapperException(string.Format("There was an error converting source {0} to target type", source.GetType().Name), ex);
            }
        }
    }

    public class MapperException : ApplicationException{
        public MapperException(string message) : base(message){}
        public MapperException(string message, Exception innerException) : base(message, innerException){}
    }

    internal class MapperConfigLoader{
        private readonly List<Mapper> _configuration = new List<Mapper>();

        private static IEnumerable<Type> GetMappers(){
            return
                from assembly in AppDomain.CurrentDomain.GetAssemblies()
                from types in assembly.GetTypes()
                where typeof (Mapper).IsAssignableFrom(types) && !types.IsAbstract
                select types;
        }

        public void ActivateMappers(){
            var types = GetMappers().ToList();

            foreach (var type in types){
                try{
                    _configuration.Add((Mapper) Activator.CreateInstance(type));
                }
                catch (Exception ex){
                    throw new MapperException("There was an error creating mapper " + type.Name, ex);
                }
            }
        }
    }

    public abstract class Mapper{
        private readonly IMapperConfiguration _configuration;

        protected Mapper(){
            Map = new SetupMapping(_configuration);
            _configuration = ObjectMapper.CurrentConfiguration;
        }

        protected Mapper(IMapperConfiguration configuration){
            _configuration = configuration;
        }

        public SetupMapping Map { get; set; }

        protected void ConvertUsing<TFrom, TTo>(Func<TFrom, TTo> conversion){
            _configuration.AddConversion(conversion);
        }

        public class SetupMapping{
            private readonly IMapperConfiguration _configuration;

            public SetupMapping(IMapperConfiguration configuration){
                _configuration = configuration;
            }

            public SetupMap<TSource, TDestination> FromTo<TSource, TDestination>() where TSource : class
                where TDestination : class{
                return new MapTo<TSource>(_configuration).To<TDestination>();
            }

            public MapTo<TSource> From<TSource>() where TSource : class{
                return new MapTo<TSource>(_configuration);
            }

            public void UsingConvention(Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>> convention){
                _configuration.AddConvention(convention);
            }

            public class SetupConventionsOnManual<TSource, TDestination> where TSource : class
                where TDestination : class{
                private readonly ManualMap<TSource, TDestination> _map;

                public SetupConventionsOnManual(ManualMap<TSource, TDestination> map){
                    _map = map;
                }

                public void IgnoreConventions(){
                    _map.UseConventionMapping = false;
                }
            }

            public class MapTo<TSource> where TSource : class{
                internal readonly IMapperConfiguration Configuration;

                public MapTo(IMapperConfiguration configuration){
                    Configuration = configuration;
                }

                public SetupMap<TSource, TDestination> To<TDestination>() where TDestination : class{
                    var manualMap = new ManualMap<TSource, TDestination>(null, true, Configuration);
                    Configuration.AddMap(typeof (TSource), typeof (TDestination), manualMap);
                    return new SetupMap<TSource, TDestination>(manualMap, Configuration);
                }
            }

            public class SetupMap<TSource, TDestination> where TSource : class where TDestination : class{
                internal readonly IMapperConfiguration Configuration;
                private readonly ManualMap<TSource, TDestination> _map;

                public SetupMap(ManualMap<TSource, TDestination> map, IMapperConfiguration configuration){
                    _map = map;
                    Configuration = configuration;
                }

                public SetupMap<TSource, TDestination> IncludeFrom<T>(){
                    Debug.Assert(typeof (TSource).IsAssignableFrom(typeof (T)), "TSource must be assignable from T in order to share a property map");
                    Configuration.AddMap(typeof (T), typeof (TDestination), _map);
                    return this;
                }

                public SetupMap<TSource, TDestination> IncludeTo<T>(){
                    Debug.Assert(typeof (TDestination).IsAssignableFrom(typeof (T)), "TDestination must be assignable from T in order to share a property map");
                    Configuration.AddMap(typeof (T), typeof (TDestination), _map);
                    return this;
                }

                public SetupMap<TSource, TDestination> AddCustomConvention(
                    Func<PropertyInfo[], PropertyInfo[], IEnumerable<dynamic>> convention){
                    _map.Conventions.Add(convention);
                    return this;
                }

                public SetupMap<TSource, TDestination> AddCustomConversion<TFrom, TTo>(Func<TFrom, TTo> conversion){
                    _map.Conversions.Add(new KeyValuePair<Type, Type>(typeof (TFrom), typeof (TTo)), new TypeConversionContainer<TFrom, TTo>(conversion));
                    return this;
                }

                public SetupConventionsOnManual<TSource, TDestination> SetManually(Action<TSource, TDestination> map){
                    _map.ObjectMap = map;
                    return new SetupConventionsOnManual<TSource, TDestination>(_map);
                }

                public SetupMap<TSource, TDestination> CreateWith(Func<TSource, TDestination> activator){
                    _map.CustomActivator = activator;
                    return this;
                }

                public SetupMap<TSource, TDestination> Set(params Expression<Func<TDestination, object>>[] properties){
                    var ignoreList = typeof (TDestination).GetProperties().Select(x => x.Name).ToList();
                    var selectedProperties = properties.Select(GetPropertyNameFromLambda);
                    ignoreList.RemoveAll(selectedProperties.Contains);
                    _map.IgnoreProperties.AddRange(ignoreList);
                    return this;
                }

                public SetupMap<TSource, TDestination> Ignore(params Expression<Func<TDestination, object>>[] properties){
                    _map.IgnoreProperties.AddRange(properties.Select(GetPropertyNameFromLambda));
                    return this;
                }

                internal static string GetPropertyNameFromLambda(Expression<Func<TDestination, object>> expression){
                    var lambda = expression as LambdaExpression;
                    Debug.Assert(lambda != null, "Not a valid lambda epression");
                    MemberExpression memberExpression;

                    if (lambda.Body is UnaryExpression){
                        var unaryExpression = lambda.Body as UnaryExpression;
                        memberExpression = unaryExpression.Operand as MemberExpression;
                    }
                    else{
                        memberExpression = lambda.Body as MemberExpression;
                    }

                    Debug.Assert(memberExpression != null, "Please provide a lambda expression like 'x => x.PropertyName'");
                    var propertyInfo = (PropertyInfo) memberExpression.Member;

                    return propertyInfo.Name;
                }
            }
        }
    }

    internal class PropertyLookup{
        public PropertyInfo Source { get; set; }
        public PropertyInfo Destination { get; set; }
        public ITypeConverter Conversion { get; set; }

        private bool Equals(PropertyLookup other){
            return Equals(Source, other.Source) && Equals(Destination, other.Destination);
        }

        public override bool Equals(object obj){
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((PropertyLookup) obj);
        }

        public override int GetHashCode(){
            unchecked{
                return ((Source != null ? Source.GetHashCode() : 0)*397) ^
                       (Destination != null ? Destination.GetHashCode() : 0);
            }
        }
    }
}