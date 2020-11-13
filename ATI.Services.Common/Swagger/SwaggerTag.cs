using System;

namespace ATI.Services.Common.Swagger
{
    [Flags]
    public enum SwaggerTag
    {
        All = 1,
        Open = 2,
        Public = 4,
        Internal = 8
    }
}
