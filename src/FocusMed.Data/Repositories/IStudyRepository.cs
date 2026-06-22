using FocusMed.Data.Entities;

namespace FocusMed.Data.Repositories;

public interface IStudyRepository
{
    Task<Study?> GetByIdAsync(int id);
    Task<Study?> GetByInstanceUidAsync(string studyInstanceUid);
    Task<List<Study>> GetStudiesByStatusAsync(StudyStatus status);
    Task AddAsync(Study study);
    Task UpdateAsync(Study study);
}
