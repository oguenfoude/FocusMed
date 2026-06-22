using FocusMed.Data.Entities;

namespace FocusMed.Data.Repositories;

public interface IDicomImageRepository
{
    Task<DicomImage?> GetBySopInstanceUidAsync(string sopInstanceUid);
    Task AddAsync(DicomImage image);
}
