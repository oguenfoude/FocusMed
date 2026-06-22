using FocusMed.Data.Entities;

namespace FocusMed.Data.Repositories;

public interface ISeriesRepository
{
    Task<Series?> GetByIdAsync(int id);
    Task<Series?> GetByInstanceUidAsync(string seriesInstanceUid);
    Task AddAsync(Series series);
    Task UpdateAsync(Series series);
}
