using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    public static class DataTypes
    {
        public const string Type_SByte = "System.SByte";
        public const string Type_Byte = "System.Byte";
        public const string Type_Short = "System.Int16";
        public const string Type_UShort = "System.UInt16";
        public const string Type_Int = "System.Int32";
        public const string Type_UInt = "System.UInt32";
        public const string Type_Long = "System.Int64";
        public const string Type_ULong = "System.UInt64";
        public const string Type_Float = "System.Single";
        public const string Type_Double = "System.Double";
        public const string Type_Bool = "System.Boolean";
        public const string Type_DateTime = "System.DateTime";
        public const string Type_Char = "System.Char";
        public const string Type_Decimal = "System.Decimal";
        public const string Type_String = "System.String";

        public static object ConvertTo(this Type type, object value)
        {
            switch (type.FullName)
            {
                case Type_String:
                    return Convert.ToString(value);
                case Type_SByte:
                    return Convert.ToSByte(value);
                case Type_Byte:
                    return Convert.ToByte(value);
                case Type_Short:
                    return Convert.ToInt16(value);
                case Type_UShort:
                    return Convert.ToUInt16(value);
                case Type_Int:
                    return Convert.ToInt32(value);
                case Type_UInt:
                    return Convert.ToUInt32(value);
                case Type_Long:
                    return Convert.ToInt64(value);
                case Type_ULong:
                    return Convert.ToUInt64(value);
                case Type_Float:
                    return Convert.ToSingle(value);
                case Type_Double:
                    return Convert.ToDouble(value);
                case Type_Bool:
                    return Convert.ToBoolean(value);
                case Type_DateTime:
                    return Convert.ToDateTime(value);
                case Type_Char:
                    return Convert.ToChar(value);
                case Type_Decimal:
                    return Convert.ToChar(value);
                default:
                    return null;
            }
        }

        public static bool IsBasicType(this Type type)
        {
            switch (type.FullName)
            {
                case Type_String:
                case Type_SByte:
                case Type_Byte:
                case Type_Short:
                case Type_UShort:
                case Type_Int:
                case Type_UInt:
                case Type_Long:
                case Type_ULong:
                case Type_Float:
                case Type_Double:
                case Type_Bool:
                case Type_DateTime:
                case Type_Char:
                case Type_Decimal:
                    return true;
                default:
                    return false;
            }
        }
    }
}
