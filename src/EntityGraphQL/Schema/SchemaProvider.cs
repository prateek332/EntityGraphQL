using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.Tasks;
using EntityGraphQL.Authorization;
using EntityGraphQL.Compiler;
using EntityGraphQL.Compiler.Util;
using EntityGraphQL.Directives;
using EntityGraphQL.Extensions;
using EntityGraphQL.Compiler.EntityQuery;
using Microsoft.Extensions.Logging;

namespace EntityGraphQL.Schema
{
    /// <summary>
    /// Builder interface to build a schema definition. The built schema definition maps an external view of your data model to you internal model.
    /// This allows your internal model to change over time while not break your external API. You can create new versions when needed.
    /// </summary>
    /// <typeparam name="TContextType">Base object graph. Ex. DbContext</typeparam>
    public class SchemaProvider<TContextType> : ISchemaProvider
    {
        public Func<string, string> SchemaFieldNamer { get; }
        protected Dictionary<string, ISchemaType> types = new();
        protected Dictionary<string, MutationType> mutations = new();
        protected Dictionary<string, IDirectiveProcessor> directives = new();

        private readonly string queryContextName;
        private readonly ILogger<SchemaProvider<TContextType>> logger;
        private readonly DefaultMethodProvider methodProvider;

        // map some types to scalar types
        protected Dictionary<Type, GqlTypeInfo> customTypeMappings;
        public SchemaProvider() : this(null, null) { }
        /// <summary>
        /// Create a new GraphQL Schema provider that defines all the types and fields etc.
        /// </summary>
        /// <param name="fieldNamer">A naming function for fields that will be used when using methods that automatically create field names e.g. SchemaType.AddAllFields()</param>
        public SchemaProvider(Func<string, string> fieldNamer = null, ILogger<SchemaProvider<TContextType>> logger = null)
        {
            SchemaFieldNamer = fieldNamer ?? SchemaBuilder.DefaultNamer;
            this.logger = logger;
            // default GQL scalar types
            types.Add("Int", new SchemaType<int>(this, "Int", "Int scalar", null, SchemaFieldNamer, false, false, true));
            types.Add("Float", new SchemaType<double>(this, "Float", "Float scalar", null, SchemaFieldNamer, false, false, true));
            types.Add("Boolean", new SchemaType<bool>(this, "Boolean", "Boolean scalar", null, SchemaFieldNamer, false, false, true));
            types.Add("String", new SchemaType<string>(this, "String", "String scalar", null, SchemaFieldNamer, false, false, true));
            types.Add("ID", new SchemaType<Guid>(this, "ID", "ID scalar", null, SchemaFieldNamer, false, false, true));

            // default custom scalar for DateTime
            types.Add("Date", new SchemaType<DateTime>(this, "Date", "Date with time scalar", null, SchemaFieldNamer, false, false, true));

            customTypeMappings = new Dictionary<Type, GqlTypeInfo> {
                {typeof(short), new GqlTypeInfo(() => Type("Int"), typeof(short))},
                {typeof(ushort), new GqlTypeInfo(() => Type("Int"), typeof(ushort))},
                {typeof(uint), new GqlTypeInfo(() => Type("Int"), typeof(uint))},
                {typeof(ulong), new GqlTypeInfo(() => Type("Int"), typeof(ulong))},
                {typeof(long), new GqlTypeInfo(() => Type("Int"), typeof(long))},
                {typeof(float), new GqlTypeInfo(() => Type("Float"), typeof(float))},
                {typeof(decimal), new GqlTypeInfo(() => Type("Float"), typeof(decimal))},
                {typeof(byte[]), new GqlTypeInfo(() => Type("String"), typeof(byte[]))},
                {typeof(bool), new GqlTypeInfo(() => Type("Boolean"), typeof(bool))},
            };

            var queryContext = new SchemaType<TContextType>(this, typeof(TContextType).Name, "Query schema", null, SchemaFieldNamer);
            queryContextName = queryContext.Name;
            types.Add(queryContext.Name, queryContext);

            // add types first as fields from the other types may refer to these types
            AddType<Models.TypeElement>("__Type", "Information about types", type =>
                {
                    type.AddAllFields();
                    type.ReplaceField("enumValues",
                        new { includeDeprecated = false },
                        (t, p) => t.EnumValues.Where(f => p.includeDeprecated ? f.IsDeprecated || !f.IsDeprecated : !f.IsDeprecated).ToList(),
                        "Enum values available on type");
                });
            AddType<Models.EnumValue>("__EnumValue", "Information about enums").AddAllFields();
            AddType<Models.InputValue>("__InputValue", "Arguments provided to Fields or Directives and the input fields of an InputObject are represented as Input Values which describe their type and optionally a default value.").AddAllFields();
            AddType<Models.Directive>("__Directive", "Information about directives").AddAllFields();
            AddType<Models.Field>("__Field", "Information about fields").AddAllFields();
            AddType<Models.SubscriptionType>("Information about subscriptions").AddAllFields();
            AddType<Models.Schema>("__Schema", "A GraphQL Schema defines the capabilities of a GraphQL server. It exposes all available types and directives on the server, as well as the entry points for query, mutation, and subscription operations.").AddAllFields();

            SetupIntrospectionTypesAndField();

            var include = new IncludeDirectiveProcessor();
            var skip = new SkipDirectiveProcessor();
            directives.Add(include.Name, include);
            directives.Add(skip.Name, skip);

            methodProvider = new DefaultMethodProvider();
        }

        public QueryResult ExecuteQuery(QueryRequest gql, TContextType context, IServiceProvider serviceProvider, ClaimsIdentity claims, ExecutionOptions options = null)
        {
            return ExecuteQueryAsync(gql, context, serviceProvider, claims, options).Result;
        }

        /// <summary>
        /// Execute a query using this schema.
        /// </summary>
        /// <param name="gql">The query</param>
        /// <param name="context">The context object. An instance of the context the schema was build from</param>
        /// <param name="serviceProvider">A service provider used for looking up dependencies of field selections and mutations</param>
        /// <param name="claims">Optional claims to check access for queries</param>
        /// <param name="options"></param>
        /// <typeparam name="TContextType"></typeparam>
        /// <returns></returns>
        public async Task<QueryResult> ExecuteQueryAsync(QueryRequest gql, TContextType context, IServiceProvider serviceProvider, ClaimsIdentity claims, ExecutionOptions options = null)
        {
            QueryResult result;
            try
            {
                var queryResult = CompileQuery(gql, claims);
                result = await queryResult.ExecuteQueryAsync(context, serviceProvider, gql.OperationName, options);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error executing QueryRequest");
                // error with the whole query
                result = new QueryResult(new GraphQLError(ex.InnerException != null ? $"{ex.Message} - {ex.InnerException.Message}" : ex.Message));
            }

            return result;
        }

        public GraphQLDocument CompileQuery(QueryRequest gql, ClaimsIdentity claims)
        {
            var graphQLCompiler = new GraphQLCompiler(this, methodProvider);
            var queryResult = graphQLCompiler.Compile(gql, claims);
            return queryResult;
        }

        private void SetupIntrospectionTypesAndField()
        {
            // evaluate Fields lazily so we don't end up in endless loop
            Type<Models.TypeElement>("__Type").ReplaceField("fields", new { includeDeprecated = false },
                (t, p) => SchemaIntrospection.BuildFieldsForType(this, t.Name).Where(f => p.includeDeprecated ? f.IsDeprecated || !f.IsDeprecated : !f.IsDeprecated).ToList(), "Fields available on type");

            ReplaceField("__schema", db => SchemaIntrospection.Make(this), "Introspection of the schema", "__Schema");
            ReplaceField("__type", new { name = ArgumentHelper.Required<string>() }, (db, p) => SchemaIntrospection.Make(this).Types.Where(s => s.Name == p.name).First(), "Query a type by name", "__Type");
        }

        /// <summary>
        /// Add a new type into the schema with TBaseType as it's context
        /// </summary>
        /// <param name="name">Name of the type</param>
        /// <param name="description">description of the type</param>
        /// <typeparam name="TBaseType"></typeparam>
        /// <returns></returns>
        public SchemaType<TBaseType> AddType<TBaseType>(string name, string description)
        {
            var tt = new SchemaType<TBaseType>(this, name, description, null, SchemaFieldNamer);
            FinishAddingType(typeof(TBaseType), name, tt);
            return tt;
        }

        public ISchemaType AddType(Type contextType, string name, string description)
        {
            var newType = (ISchemaType)Activator.CreateInstance(typeof(SchemaType<>).MakeGenericType(contextType), this, contextType, name, description, null, SchemaFieldNamer, false, false, false);
            FinishAddingType(contextType, name, newType);
            return newType;
        }

        private void FinishAddingType(Type contextType, string name, ISchemaType tt)
        {
            var attributes = contextType.GetTypeInfo().GetCustomAttributes(typeof(GraphQLAuthorizeAttribute), true).Cast<GraphQLAuthorizeAttribute>();
            if (attributes.Any())
                tt.AuthorizeClaims = new RequiredClaims(attributes);
            types.Add(name, tt);
        }

        public void AddType<TBaseType>(string name, string description, Action<SchemaType<TBaseType>> updateFunc)
        {
            updateFunc(AddType<TBaseType>(name, description));
        }

        public ISchemaProvider RemoveType<TType>()
        {
            return RemoveType(Type(typeof(TType)).Name);
        }

        public ISchemaProvider RemoveType(string schemaType)
        {
            types.Remove(schemaType);
            return this;
        }

        /// <summary>
        /// Add a GQL Input type to the schema. Input types are objects used in arguments of fields or mutations
        /// </summary>
        /// <param name="name"></param>
        /// <param name="description"></param>
        /// <typeparam name="TBaseType"></typeparam>
        /// <returns></returns>
        public SchemaType<TBaseType> AddInputType<TBaseType>(string name, string description)
        {
            return (SchemaType<TBaseType>)AddInputType(typeof(TBaseType), name, description);
        }

        public ISchemaType AddInputType(Type type, string name, string description)
        {
            var newType = (ISchemaType)Activator.CreateInstance(typeof(SchemaType<>).MakeGenericType(type), this, type, name, description, null, SchemaFieldNamer, true, false, false);
            var attributes = type.GetTypeInfo().GetCustomAttributes(typeof(GraphQLAuthorizeAttribute), true).Cast<GraphQLAuthorizeAttribute>();
            if (attributes.Any())
                newType.AuthorizeClaims = new RequiredClaims(attributes);

            types.Add(name, newType);
            return newType;
        }

        /// <summary>
        /// Add any methods marked with GraphQLMutationAttribute in the given type to the schema. Names are added as lowerCaseCamel`
        /// </summary>
        /// <param name="mutationClassInstance"></param>
        /// <typeparam name="TType"></typeparam>
        public void AddMutationFrom<TType>(TType mutationClassInstance)
        {
            foreach (var method in mutationClassInstance.GetType().GetMethods())
            {
                if (method.GetCustomAttribute(typeof(GraphQLMutationAttribute)) is GraphQLMutationAttribute attribute)
                {
                    var isAsync = method.GetCustomAttribute(typeof(AsyncStateMachineAttribute)) != null;
                    string name = SchemaFieldNamer(method.Name);
                    var claims = method.GetCustomAttributes(typeof(GraphQLAuthorizeAttribute)).Cast<GraphQLAuthorizeAttribute>();
                    var requiredClaims = new RequiredClaims(claims);
                    var actualReturnType = GetTypeFromMutationReturn(isAsync ? method.ReturnType.GetGenericArguments()[0] : method.ReturnType);
                    var typeName = GetSchemaTypeForDotnetType(actualReturnType).Name;
                    var returnType = new GqlTypeInfo(() => GetSchemaType(typeName), actualReturnType);
                    var mutationType = new MutationType(this, name, returnType, mutationClassInstance, method, attribute.Description, requiredClaims, isAsync, SchemaFieldNamer);
                    mutations[name] = mutationType;
                }
            }
        }

        public ISchemaType GetSchemaType(string typeName)
        {
            if (types.ContainsKey(typeName))
                return types[typeName];

            return null;
        }

        public bool HasMutation(string method)
        {
            return mutations.ContainsKey(method);
        }

        /// <summary>
        /// Add a mapping from a Dotnet type to a GQL schema type. Make sure you have added the GQL type
        /// in the schema as a Scalar type or full type
        /// </summary>
        /// <param name="gqlType">The GQL schema type in full form. E.g. [Int!]!, [Int], Int, etc.</param>
        /// <typeparam name="TFrom"></typeparam>
        public void AddTypeMapping<TFrom>(string gqlType)
        {
            var typeInfo = GqlTypeInfo.FromGqlType(this, typeof(TFrom), gqlType);
            // add mapping
            customTypeMappings.Add(typeof(TFrom), typeInfo);
            SetupIntrospectionTypesAndField();
        }

        public GqlTypeInfo GetCustomTypeMapping(Type dotnetType)
        {
            if (customTypeMappings.ContainsKey(dotnetType))
                return customTypeMappings[dotnetType];
            return null;
        }

        /// <summary>
        /// Adds a new type into the schema. The name defaults to the TBaseType name
        /// </summary>
        /// <param name="description"></param>
        /// <typeparam name="TBaseType"></typeparam>
        /// <returns></returns>
        public SchemaType<TBaseType> AddType<TBaseType>(string description)
        {
            var name = typeof(TBaseType).Name;
            return AddType<TBaseType>(name, description);
        }

        /// <summary>
        /// Add a field to the root type. This is where you define top level objects/names that you can query.
        /// The name defaults to the MemberExpression from selection modified by the FieldNamer
        /// </summary>
        /// <param name="selection"></param>
        /// <param name="description"></param>
        /// <param name="returnSchemaType"></param>
        public Field AddField<TReturn>(Expression<Func<TContextType, TReturn>> selection, string description, string returnSchemaType = null)
        {
            var exp = ExpressionUtil.CheckAndGetMemberExpression(selection);
            return AddField(SchemaFieldNamer(exp.Member.Name), selection, description, returnSchemaType);
        }

        /// <summary>
        /// Add a field to the root type. This is where you define top level objects/names that you can query.
        /// Note the name you use is case sensitive. We recommend following GraphQL and useCamelCase as this library will for methods that use Expressions.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="selection"></param>
        /// <param name="description"></param>
        /// <param name="returnSchemaType"></param>
        public Field AddField<TReturn>(string name, Expression<Func<TContextType, TReturn>> selection, string description, string returnSchemaType = null)
        {
            return Type<TContextType>().AddField(name, selection, description, returnSchemaType);
        }

        public Field ReplaceField<TParams, TReturn>(Expression<Func<TContextType, object>> selection, TParams argTypes, Expression<Func<TContextType, TParams, TReturn>> selectionExpression, string description, string returnSchemaType = null)
        {
            var exp = ExpressionUtil.CheckAndGetMemberExpression(selection);
            var name = SchemaFieldNamer(exp.Member.Name);
            Type<TContextType>().RemoveField(name);
            return Type<TContextType>().AddField(name, argTypes, selectionExpression, description, returnSchemaType);
        }

        public Field ReplaceField<TReturn>(string name, Expression<Func<TContextType, TReturn>> selectionExpression, string description, string returnSchemaType = null)
        {
            Type<TContextType>().RemoveField(name);
            return Type<TContextType>().AddField(name, selectionExpression, description, returnSchemaType);
        }

        public Field ReplaceField<TParams, TReturn>(string name, TParams argTypes, Expression<Func<TContextType, TParams, TReturn>> selectionExpression, string description, string returnSchemaType = null)
        {
            Type<TContextType>().RemoveField(name);
            return Type<TContextType>().AddField(name, argTypes, selectionExpression, description, returnSchemaType);
        }

        /// <summary>
        /// Add a field with arguments.
        /// {
        ///     field(arg: val) {}
        /// }
        /// Note the name you use is case sensitive. We recommend following GraphQL and useCamelCase as this library will for methods that use Expressions.
        /// </summary>
        /// <param name="name">Field name</param>
        /// <param name="argTypes">Anonymous object defines the names and types of each argument</param>
        /// <param name="selectionExpression">The expression that selects the data from TContextType using the arguments</param>
        /// <param name="returnSchemaType">The schema type to return, it defines the fields available on the return object. If null, defaults to TReturn type mapped in the schema.</param>
        /// <typeparam name="TParams">Type describing the arguments</typeparam>
        /// <typeparam name="TReturn">The return entity type that is mapped to a type in the schema</typeparam>
        /// <returns></returns>
        public Field AddField<TParams, TReturn>(string name, TParams argTypes, Expression<Func<TContextType, TParams, TReturn>> selectionExpression, string description, string returnSchemaType = null)
        {
            return Type<TContextType>().AddField(name, argTypes, selectionExpression, description, returnSchemaType);
        }
        /// <summary>
        /// Add a field to the root query.
        /// Note the name you use is case sensistive. We recommend following GraphQL and useCamelCase as this library will for methods that use Expressions.
        /// </summary>
        /// <param name="field"></param>
        public Field AddField(Field field)
        {
            return types[queryContextName].AddField(field);
        }

        /// <summary>
        /// Get registered type by TType name
        /// </summary>
        /// <typeparam name="TType"></typeparam>
        /// <returns></returns>
        public SchemaType<TType> Type<TType>()
        {
            // look up by the actual type not the name
            var schemaType = (SchemaType<TType>)Type(typeof(TType));
            return schemaType;
        }

        public void UpdateType<TType>(Action<SchemaType<TType>> updateFunc) => updateFunc(Type<TType>());

        public ISchemaType Type(Type dotnetType)
        {
            // look up by the actual type not the name
            var schemaType = types.Values.FirstOrDefault(t => t.TypeDotnet == dotnetType);
            if (schemaType == null && customTypeMappings.ContainsKey(dotnetType))
            {
                schemaType = customTypeMappings[dotnetType].SchemaType;
            }
            if (schemaType == null)
                throw new EntityGraphQLCompilerException($"No schema type found for dotnet type {dotnetType.Name}. Make sure you add it or add a type mapping");
            return schemaType;
        }
        public SchemaType<TType> Type<TType>(string typeName)
        {
            var schemaType = (SchemaType<TType>)types[typeName];
            return schemaType;
        }
        public ISchemaType Type(string typeName)
        {
            var schemaType = types[typeName];
            return schemaType;
        }

        // ISchemaProvider interface
        public Type ContextType { get { return types[queryContextName].TypeDotnet; } }
        public bool TypeHasField(string typeName, string identifier, IEnumerable<string> fieldArgs, ClaimsIdentity claims)
        {
            if (!types.ContainsKey(typeName))
                return false;
            var t = types[typeName];
            if (!t.HasField(identifier))
            {
                if ((fieldArgs == null || !fieldArgs.Any()) && t.HasField(identifier))
                {
                    var field = t.GetField(identifier, claims);
                    if (field != null)
                    {
                        // if there are defaults for all, continue
                        if (field.RequiredArgumentNames.Any())
                        {
                            throw new EntityGraphQLCompilerException($"Field '{identifier}' missing required argument(s) '{string.Join(", ", field.RequiredArgumentNames)}'");
                        }
                        return true;
                    }
                    else
                    {
                        throw new EntityGraphQLCompilerException($"Field '{identifier}' not found on current context '{typeName}'");
                    }
                }
                return false;
            }
            return true;
        }

        public bool TypeHasField(Type type, string identifier, IEnumerable<string> fieldArgs, ClaimsIdentity claims)
        {
            return TypeHasField(type.Name, identifier, fieldArgs, claims);
        }

        public List<ISchemaType> EnumTypes()
        {
            return types.Values.Where(t => t.IsEnum).ToList();
        }

        public IField GetActualField(string typeName, string identifier, ClaimsIdentity claims)
        {
            IField field = null;
            if (types.ContainsKey(typeName) && types[typeName].HasField(identifier))
            {
                if (!AuthUtil.IsAuthorized(claims, types[typeName].AuthorizeClaims))
                    throw new EntityGraphQLAccessException($"You do not have access to the '{typeName}' type. You require any of the following security claims [{string.Join(", ", types[typeName].AuthorizeClaims.Claims.SelectMany(r => r))}]");
                field = types[typeName].GetField(identifier, claims);
            }
            else if (typeName == queryContextName && types[queryContextName].HasField(identifier))
                field = types[queryContextName].GetField(identifier, claims);
            else if (mutations.Keys.Any(k => k.ToLower() == identifier.ToLower()))
                field = mutations.First(k => k.Key.ToLower() == identifier.ToLower()).Value;

            if (field == null)
                throw new EntityGraphQLCompilerException($"Field {identifier} not found on type {typeName}");

            if (!AuthUtil.IsAuthorized(claims, field.ReturnType.SchemaType.AuthorizeClaims))
                throw new EntityGraphQLAccessException($"You do not have access to the '{field.ReturnType.SchemaType.Name}' type. You require any of the following security claims [{string.Join(", ", field.ReturnType.SchemaType.AuthorizeClaims.Claims.SelectMany(r => r))}]");

            return field;
        }

        public IField GetFieldOnContext(Expression context, string fieldName, ClaimsIdentity claims)
        {
            if (context.Type == ContextType && mutations.ContainsKey(fieldName))
            {
                var mutation = mutations[fieldName];
                if (!AuthUtil.IsAuthorized(claims, mutation.AuthorizeClaims))
                {
                    throw new EntityGraphQLAccessException($"You do not have access to mutation '{fieldName}'. You require any of the following security claims [{string.Join(", ", mutation.AuthorizeClaims.Claims.SelectMany(r => r))}]");
                }
                return mutation;
            }

            var schemaType = GetSchemaTypeForDotnetType(context.Type);
            if (types.ContainsKey(schemaType.Name))
            {
                var field = types[schemaType.Name].GetField(fieldName, claims);
                return field;
            }
            throw new EntityGraphQLCompilerException($"No field or mutation '{fieldName}' found in schema.");
        }

        /// <summary>
        /// Give a Dotnet type it finds the matching schema type name
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public ISchemaType GetSchemaTypeForDotnetType(Type type)
        {
            type = GetTypeFromMutationReturn(type);

            if (type.IsEnumerableOrArray())
            {
                type = type.GetGenericArguments()[0];
            }

            if (customTypeMappings.ContainsKey(type))
                return customTypeMappings[type].SchemaType;

            if (type == types[queryContextName].TypeDotnet)
                return types[queryContextName];

            foreach (var eType in types.Values)
            {
                if (eType.TypeDotnet == type)
                    return eType;
            }
            throw new EntityGraphQLCompilerException($"No mapped entity found for type '{type}'");
        }

        /// <summary>
        /// Return the actual return type of a mutation - strips out the Expression<Func<>>
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public Type GetTypeFromMutationReturn(Type type)
        {
            if (type.GetTypeInfo().BaseType == typeof(LambdaExpression))
            {
                // This should be Expression<Func<Context, ReturnType>>
                type = type.GetGenericArguments()[0].GetGenericArguments()[1];
            }

            return type;
        }

        public bool HasType(string typeName)
        {
            return types.ContainsKey(typeName);
        }

        public bool HasType(Type type)
        {
            if (type == types[queryContextName].TypeDotnet)
                return true;

            foreach (var eType in types.Values)
            {
                if (eType.TypeDotnet == type)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Builds a GraphQL schema file
        /// </summary>
        /// <returns></returns>
        public string GetGraphQLSchema()
        {
            var extraMappings = customTypeMappings.ToDictionary(k => k.Key, v => v.Value);
            return SchemaGenerator.Make(this);
        }

        public ISchemaType AddScalarType(Type clrType, string gqlTypeName, string description)
        {
            var schemaType = (ISchemaType)Activator.CreateInstance(typeof(SchemaType<>).MakeGenericType(clrType), this, gqlTypeName, description, false, false, true);
            types.Add(gqlTypeName, schemaType);
            return schemaType;
        }

        public ISchemaType AddScalarType<TType>(string gqlTypeName, string description)
        {
            var schemaType = new SchemaType<TType>(this, gqlTypeName, description, null, SchemaFieldNamer, false, false, true);
            types.Add(gqlTypeName, schemaType);
            return schemaType;
        }

        public IEnumerable<Field> GetQueryFields()
        {
            return types[queryContextName].GetFields();
        }

        public IEnumerable<ISchemaType> GetNonContextTypes()
        {
            return types.Values.Where(s => s.Name != queryContextName).ToList();
        }

        public IEnumerable<ISchemaType> GetScalarTypes()
        {
            return types.Values.Where(t => t.IsScalar);
        }

        public IEnumerable<MutationType> GetMutations()
        {
            return mutations.Values.ToList();
        }

        /// <summary>
        /// Remove type and any field that returns that type
        /// </summary>
        /// <typeparam name="TSchemaType"></typeparam>
        public void RemoveTypeAndAllFields<TSchemaType>()
        {
            this.RemoveTypeAndAllFields(typeof(TSchemaType).Name);
        }
        /// <summary>
        /// Remove type and any field that returns that type
        /// </summary>
        /// <param name="typeName"></param>
        public void RemoveTypeAndAllFields(string typeName)
        {
            foreach (var context in types.Values)
            {
                RemoveFieldsOfType(typeName, context);
            }
            types.Remove(typeName);
        }

        private void RemoveFieldsOfType(string schemaType, ISchemaType contextType)
        {
            foreach (var field in contextType.GetFields().ToList())
            {
                if (field.ReturnType.SchemaType.Name == schemaType)
                {
                    contextType.RemoveField(field.Name);
                }
            }
        }

        public ISchemaType AddEnum(string name, Type type, string description)
        {
            var schemaType = new SchemaType<object>(this, type, name, description, null, SchemaFieldNamer, false, true);
            var attributes = type.GetTypeInfo().GetCustomAttributes(typeof(GraphQLAuthorizeAttribute), true).Cast<GraphQLAuthorizeAttribute>();
            if (attributes.Any())
                schemaType.AuthorizeClaims = new RequiredClaims(attributes);
            types.Add(name, schemaType);
            return schemaType.AddAllFields();
        }

        public IDirectiveProcessor GetDirective(string name)
        {
            if (directives.ContainsKey(name))
                return directives[name];
            throw new EntityGraphQLCompilerException($"Directive {name} not defined in schema");
        }
        public IEnumerable<IDirectiveProcessor> GetDirectives()
        {
            return directives.Values.ToList();
        }
        public void AddDirective(IDirectiveProcessor directive)
        {
            if (directives.ContainsKey(directive.Name))
                throw new EntityGraphQLCompilerException($"Directive {directive.Name} already exists on schema");
            directives.Add(directive.Name, directive);
        }

        public void PopulateFromContext(bool autoCreateIdArguments, bool autoCreateEnumTypes)
        {
            SchemaBuilder.FromObject(this, autoCreateIdArguments, autoCreateEnumTypes, SchemaFieldNamer);
        }

        public void UpdateQueryType(Action<SchemaType<TContextType>> updateFunc)
        {
            updateFunc(Type<TContextType>());
        }
    }
}