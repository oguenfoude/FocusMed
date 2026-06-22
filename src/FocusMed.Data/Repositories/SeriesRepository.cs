using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Data.Repositories;

public class SeriesRepository : ISeriesRepository
{
    private readonly FocusMedDbContext _context;

    public SeriesRepository(FocusMedDbContext context)
    {
        _context = context;
    }

    public async Task<Series?> GetByIdAsync(int id)
    {
        return await _context.Series.FindAsync(id);
    }

    public async Task<Series?> GetByInstanceUidAsync(string seriesInstanceUid)
    {
        return await _context.Series.FirstOrDefaultAsync(s => s.SeriesInstanceUid == seriesInstanceUid);
    }

    public async Task AddAsync(Series series)
    {
        await _context.Series.AddAsync(series);
    }

    public Task UpdateAsync(Series series)
    {
        _context.Series.Update(series);
        return Task.CompletedTask;
    }
}
