using Dapper;

namespace ATI.Services.Common.Sql
{
    public static class EmptyFields
    {
        public static DynamicParameters DynamicParameters => new DynamicParameters();
    }
}
