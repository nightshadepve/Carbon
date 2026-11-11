using System;

namespace Oxide.Plugins
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class InfoAttribute : Attribute
    {
        public InfoAttribute(string title, string author, string version) { }
        public InfoAttribute(string title, string author, double version) { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DescriptionAttribute : Attribute
    {
        public DescriptionAttribute(string description) { }
    }
}
