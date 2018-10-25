﻿using CosmosManager.Domain;

namespace CosmosManager.Interfaces
{
    public interface IQueryStatementParser
    {
        string OrginalQuery { get; }

        string CleanQuery(string query);

        QueryParts Parse(string query);
    }
}