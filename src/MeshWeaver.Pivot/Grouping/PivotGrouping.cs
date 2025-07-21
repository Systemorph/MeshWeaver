namespace MeshWeaver.Pivot.Grouping
{
    public record PivotGrouping<TGroup, TObject>
    {
        public PivotGrouping(TGroup identity, TObject obj, object? key)
        {
            IdentityWithOrderKey = new IdentityWithOrderKey<TGroup>(identity, key);
            Object = obj;
        }

        public TObject Object { get; }

        public IdentityWithOrderKey<TGroup> IdentityWithOrderKey { get; }

        public TGroup Identity => IdentityWithOrderKey.Identity;

        public object? OrderKey => IdentityWithOrderKey.OrderKey;
    }

    // rename to sorting key
    public record IdentityWithOrderKey<TGroup>(TGroup Identity, object? OrderKey)
    {
        public virtual bool Equals(IdentityWithOrderKey<TGroup>? other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return EqualityComparer<TGroup>.Default.Equals(Identity, other.Identity);
        }

        public override int GetHashCode()
        {
            return Identity == null ? 0 : EqualityComparer<TGroup>.Default.GetHashCode(Identity);
        }
    }
}
