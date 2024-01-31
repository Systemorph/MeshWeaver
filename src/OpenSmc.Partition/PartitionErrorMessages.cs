namespace OpenSmc.Partition;

public static class PartitionErrorMessages
{
    public const string PartitionMustBeSet = "Partition key must be set.";
    public const string PartitionKeyMismatch = "Partition key property {0} of type {1} is not the same as input partition key {2}.";
    public const string AmbiguousPartitions = "Type {0} has several partition keys, which is not allowed.";
    public const string PartitionIdIsNotSpecified = "PartitionId for type {0} is not specified.";
    public const string MissingIdentityProperties = "Please specify all identity properties for type {0} which is specified in partition. Missing properties are: {1}.";
    public const string MissingIdentityPropertiesAndNotSameTypes = "Identity properties are missing in type {0} and it is impossible to compare it to instance of type {1}.";
    public const string PartitionIdIsNotFoundAndNotCreatable = "Given partition is not found in db or it has partition Id of type {0} and cannot be created automatically.";
    public const string DifferentPartitionKeyTypes = "You are not allowed to use different types of keys for partition {0} in the same request. Current is: {1} of type {2}, attempt to be replaced with: {3} of type {4}.";
    public const string PartitionTypeMismatch = "Input object {0} of type {1} is an object, where Partition type is {2}.";
    public const string AttemptToOverridePartitionIdentity = "You cannot override existing partition of type {0} which has the same key {1}.";
    public const string AttemptToOverridePartitionKey = "You cannot override existing partition of type {0} with key {1} by key {2}.";
    public const string PartitionIdTypeMismatch = "Input object {0} of type {1} is not of Id type {2} as partitionId property of type {3}.";
    public const string NoPartitionTypeForName = "There is no partition type with name {0}.";
    public const string UnmappedPartitionType = "Unmapped partition of type {0} was used.";
    public const string UnableToCreatePartition = "Unable to create partition of type {0}";
    public const string NoPartitionKeySetter = "Setter for Partition Id for type {0} is not available.";
    public const string PartitionWasNotFound = "There is no partition {0} with Id {1}.";
    public const string NoPartitionProperty = "Type {0} have partition key property {1} of type {2} with no setter.";
    public const string TypeHasNoPartitionKey = "Type {0} has no partition key.";
    public const string PartitionKeyPropertyHasIdentityAttribute = "Wrong setup. Property {0} in type {1} which is marked as partition key has IdentityProperty attribute.";
    public const string PartitionIdPropertyHasIdentityAttribute = "Wrong setup. Property {0} in type {1} which is marked as partition id has IdentityProperty attribute.";
}
