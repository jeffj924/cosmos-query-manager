namespace CosmosManager.Domain
{
    public static class Constants
    {
        public static class EventId
        {
            public const int COSMOSDB = 99;
            public const int REQUEST_RESPONSE = 100;
        }

        public static class QueryKeywords
        {
            public const string SELECT = "SELECT";
            public const string UPDATE = "UPDATE";
            public const string DELETE = "DELETE";
            public const string FROM = "FROM";
            public const string TRANSACTION = "ASTRANSACTION";
            public const string WHERE = "WHERE";
            public const string SET = "SET";
            public const string ROLLBACK = "ROLLBACK";
        }
    }
}