namespace Aggregator.Promotion.Application;

public sealed record PromotionResponseResult<TResponse>(TResponse Response, bool Replayed)
    where TResponse : class;
