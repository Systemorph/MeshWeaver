namespace OpenSmc.Scopes.Proxy
{
    public record ScopeBuilderWithType<TScopeBuilder> : ScopeBuilder
    {
        public ScopeBuilderWithType(ScopeBuilder scopeBuilder) : base(scopeBuilder)
        {
        }

        public ScopeBuilderWithType(IInternalScopeFactory factory)
            : base(factory)
        {

        }

        public TScopeBuilder WithContext(string context)
        {
            return (TScopeBuilder)(object) (this with { Context = context });
        }
        public TScopeBuilder WithStorage(object storage)
        {
            return (TScopeBuilder)(object)(this with
                                           {
                                               Storage = storage
                                           });
        }


    }

    public record ScopeBuilder
    {
        protected IInternalScopeFactory Factory { get; }
        protected string Context { get; init; }
        protected IEnumerable<object> Identities { get; init; }
        protected object Storage { get; init; }

        public ScopeBuilder(IInternalScopeFactory factory)
        {
            Factory = factory;
        }

        protected IEnumerable<TScope> CreateScopes<TScope>() => Factory.CreateScopes(typeof(TScope), Identities, Storage, null, Context, true).Cast<TScope>();
    }

    public record ScopeBuilderForScope<TScope> : ScopeBuilderWithType<ScopeBuilderForScope<TScope>>
    {
        private Func<TScope> InnerFactory { get; init; }

        public ScopeBuilderForScope(IInternalScopeFactory factory, IEnumerable<object> identities, string context, object storage)
            : base(factory)
        {
            Identities = identities;
            Context = context;
            Storage = storage;
        }


        public ScopeBuilderForScope<TScope> WithFactory(Func<TScope> factory)
        {
            return this with { InnerFactory = factory };
        }

        internal IEnumerable<TScope> ToScopes() => 
            Factory.GetOrCreate(typeof(TScope), Identities, Storage, Context, InnerFactory).Cast<TScope>();
    }

    public record ScopeBuilderSingleton : ScopeBuilderWithType<ScopeBuilderSingleton>
    {
        public virtual TScope ToScope<TScope>()
            where TScope : class, IScope
        {
            return CreateScopes<TScope>().Single();
        }

        public ScopeBuilderSingleton(IInternalScopeFactory factory)
            : base(factory)
        {
        }

        public ScopeBuilderSingleton<TStorage> WithStorage<TStorage>(TStorage storage) => new(storage,this);
    }
    public record ScopeBuilderSingleton<TStorage> : ScopeBuilderWithType<ScopeBuilderSingleton<TStorage>>
    {
        public virtual TScope ToScope<TScope>()
            where TScope : class, IScopeWithStorage<TStorage>
        {
            return CreateScopes<TScope>().Single();
        }

        public ScopeBuilderSingleton(object storage, IInternalScopeFactory factory)
            : base(factory)
        {
            Storage = storage;
        }

        public ScopeBuilderSingleton(object storage, ScopeBuilder scopeBuilder)
            : base(scopeBuilder)
        {
            Storage = storage;
        }
    }

    public record ScopeBuilderWithIdentity<TIdentity> : ScopeBuilder
    {
        public ScopeBuilderWithIdentity(ScopeBuilder scopeBuilder)
            : base(scopeBuilder)
        {
        }

        public ScopeBuilderWithIdentity(IEnumerable<object> identities, IInternalScopeFactory factory)
            : base(factory)
        {
            Identities = identities;
        }
        public IList<TScope> ToScopes<TScope>() where TScope : IScope<TIdentity>
        {
            return CreateScopes<TScope>().ToArray();
        }
        public ScopeBuilder<TIdentity,TStorage> WithStorage<TStorage>(TStorage storage) => new(storage, this);

    }

    public record ScopeBuilder<TIdentity, TStorage> : ScopeBuilder
    {
        public ScopeBuilder(object storage, ScopeBuilder scopeBuilder)
            : base(scopeBuilder)
        {
            Storage = storage;
        }

        public IList<TScope> ToScopes<TScope>() where TScope:IScope
        {
            return CreateScopes<TScope>().ToArray();
        }

        public ScopeBuilder(IEnumerable<object> identities, object storage, IInternalScopeFactory factory)
            : base(factory)
        {
            Identities = identities;
            Storage = storage;
        }
    }
}