#ifndef CACHINGLIB_H
#define CACHINGLIB_H

#ifdef CACHINGLIB_EXPORTS
#define CACHINGLIB_API __declspec(dllexport)
#else
#define CACHINGLIB_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

  struct DoubleKeyValuePair {
    char* key;
    double value;
  };

  CACHINGLIB_API void CreateMap(const char* id);
  CACHINGLIB_API void CreateSet(const char* id);
  CACHINGLIB_API bool TryAddDoubleToMap(const char* id, const char* key, double value);
  CACHINGLIB_API bool TryAddStringToMap(const char* id, const char* key, const char *value);
  CACHINGLIB_API bool TryAddStringToSet(const char* id, const char* key);
  CACHINGLIB_API bool TryRemoveFromMap(const char* id, const char* key);
  CACHINGLIB_API bool TryRemoveFromSet(const char* id, const char* key);
  CACHINGLIB_API bool IsInMap(const char* id, const char* key);
  CACHINGLIB_API bool IsInSet(const char* id, const char* key);
  CACHINGLIB_API long GetMapSize(const char* id);
  CACHINGLIB_API long GetSetSize(const char* id);
  CACHINGLIB_API const char* GetStringMapValue(const char* id, const char* key);
  CACHINGLIB_API double GetDoubleMapValue(const char* id, const char* key);
  CACHINGLIB_API DoubleKeyValuePair* GetDoubleMapEntries(const char* id, int *size);

#ifdef __cplusplus
}
#endif

#endif // CACHINGLIB_H