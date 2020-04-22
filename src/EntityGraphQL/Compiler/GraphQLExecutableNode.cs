using System;

namespace EntityGraphQL.Compiler
{
    public abstract class GraphQLExecutableNode
    {
        public abstract object Execute<TContext>(TContext context, IServiceProvider serviceProvider);
    }
}