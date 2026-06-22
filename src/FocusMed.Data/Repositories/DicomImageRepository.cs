using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Data.Repositories;

public class DicomImageRepository : IDicomImageRepository
{
    private readonly FocusMedDbContext _context;

    public DicomImageRepository(FocusMedDbContext context)
    {
        _context = context;
    }

    public async Task<DicomImage?> GetBySopInstanceUidAsync(string sopInstanceUid)
    {
        return await _context.DicomImages.FirstOrDefaultAsync(i => i.SopInstanceUid == sopInstanceUid);
    }

    public async Task AddAsync(DicomImage image)
    {
        await _context.DicomImages.AddAsync(image);
    }
}
