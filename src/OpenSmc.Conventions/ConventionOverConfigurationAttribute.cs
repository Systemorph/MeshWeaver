using System.Reflection;

namespace OpenSmc.Conventions
{
    public enum InheritanceMode
    {
        Inherited,
        NotInherited,
    }

    public abstract class ConventionOverConfigurationAttribute : Attribute
    {
        public readonly InheritanceMode InheritanceMode;

        public ConventionOverConfigurationAttribute(InheritanceMode inheritanceMode)
        {
            InheritanceMode = inheritanceMode;
        }

        public virtual bool AppliesTo(MemberInfo memberInfo, MemberInfo declaringMember)
        {
            switch (InheritanceMode)
            {
                case InheritanceMode.NotInherited:
                    return memberInfo == declaringMember;

            }
            return true;
        }
    }
}