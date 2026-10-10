using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace SqlLense.Vsix.Options
{
    /// <summary>Shows enum values in the options grid by their <see cref="DescriptionAttribute"/>.</summary>
    internal sealed class EnumDescriptionConverter<TEnum> : EnumConverter
        where TEnum : struct, Enum
    {
        public EnumDescriptionConverter()
            : base(typeof(TEnum))
        {
        }

        public static string Describe(TEnum value) =>
            typeof(TEnum).GetField(value.ToString())?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? value.ToString();

        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
            destinationType == typeof(string) && value is TEnum typed ? Describe(typed) : base.ConvertTo(context, culture, value, destinationType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value is string text)
            {
                foreach (var candidate in Enum.GetValues(typeof(TEnum)).Cast<TEnum>())
                {
                    if (Describe(candidate) == text)
                    {
                        return candidate;
                    }
                }
            }

            return base.ConvertFrom(context, culture, value);
        }
    }
}
