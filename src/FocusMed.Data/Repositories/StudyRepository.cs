using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Data.Repositories;

public class StudyRepository : IStudyRepository
{
    private readonly FocusMedDbContext _context;

    public StudyRepository(FocusMedDbContext context)
    {
        _context = context;
    }

    public async Task<Study?> GetByIdAsync(int id)
    {
        return await _context.Studies.FindAsync(id);
    }

    public async Task<Study?> GetByInstanceUidAsync(string studyInstanceUid)
    {
        return await _context.Studies.FirstOrDefaultAsync(s => s.StudyInstanceUid == studyInstanceUid);
    }

    public async Task<List<Study>> GetStudiesByStatusAsync(StudyStatus status)
    {
        return await _context.Studies.Where(s => s.Status == status).ToListAsync();
    }

    public async Task AddAsync(Study study)
    {
        await _context.Studies.AddAsync(study);
    }

    public Task UpdateAsync(Study study)
    {
        _context.Studies.Update(study);
        return Task.CompletedTask;
    }
}
