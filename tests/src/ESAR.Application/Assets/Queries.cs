using Esar.Application.Abstractions;
using Esar.Application.Common;
using MediatR;

namespace Esar.Application.Assets;

public record GetAssetByIdQuery(Guid Id) : IRequest<AssetDetailDto?>;

public class GetAssetByIdHandler : IRequestHandler<GetAssetByIdQuery, AssetDetailDto?>
{
    private readonly IUnitOfWork _uow;
    public GetAssetByIdHandler(IUnitOfWork uow) => _uow = uow;

    public async Task<AssetDetailDto?> Handle(GetAssetByIdQuery request, CancellationToken ct)
    {
        var asset = await _uow.Assets.GetWithDetailsAsync(request.Id, ct);
        return asset is null ? null : AssetDetailDto.FromDetailed(asset);
    }
}

public record SearchAssetsQuery(AssetSearchCriteria Criteria) : IRequest<PagedResult<AssetDto>>;

public class SearchAssetsHandler : IRequestHandler<SearchAssetsQuery, PagedResult<AssetDto>>
{
    private readonly IUnitOfWork _uow;
    public SearchAssetsHandler(IUnitOfWork uow) => _uow = uow;

    public async Task<PagedResult<AssetDto>> Handle(SearchAssetsQuery request, CancellationToken ct)
    {
        var result = await _uow.Assets.SearchAsync(request.Criteria, ct);
        return new PagedResult<AssetDto>
        {
            Items = result.Items.Select(AssetDto.From).ToList(),
            Page = result.Page,
            PageSize = result.PageSize,
            TotalCount = result.TotalCount
        };
    }
}

public record GetAssetHistoryQuery(Guid AssetId) : IRequest<IReadOnlyList<AssetHistoryDto>>;

public class GetAssetHistoryHandler : IRequestHandler<GetAssetHistoryQuery, IReadOnlyList<AssetHistoryDto>>
{
    private readonly IUnitOfWork _uow;
    public GetAssetHistoryHandler(IUnitOfWork uow) => _uow = uow;

    public async Task<IReadOnlyList<AssetHistoryDto>> Handle(GetAssetHistoryQuery request, CancellationToken ct)
    {
        var history = await _uow.AssetHistories.ListAsync(h => h.AssetId == request.AssetId, ct);
        return history.OrderByDescending(h => h.ChangedAt)
            .Select(h => new AssetHistoryDto(h.FieldName, h.OldValue, h.NewValue, h.ChangedBy,
                h.Source?.ToString(), h.ChangedAt))
            .ToList();
    }
}
