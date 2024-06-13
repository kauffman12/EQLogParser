#include "pch.h"
#include "CachingLib.h"
#include <boost/unordered/unordered_flat_map.hpp>
#include <boost/unordered/unordered_flat_set.hpp>
#include <boost/functional/hash.hpp>
#include <combaseapi.h> // For CoTaskMemAlloc and CoTaskMemFree
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <variant>
#include <vector>

const double NULL_DOUBLE = std::numeric_limits<double>::lowest();

using MapValue = std::variant<double, std::string>;

using FlatMap = boost::unordered_flat_map<
  std::string,
  MapValue,
  boost::hash<std::string>,
  std::equal_to<std::string>,
  std::allocator<std::pair<const std::string, std::string>>
>;

using FlatSet = boost::unordered_flat_set<
  std::string,
  boost::hash<std::string>,
  std::equal_to<std::string>,
  std::allocator<std::string>
>;

std::unordered_map<std::string, FlatMap> maps;
std::unordered_map<std::string, FlatSet> sets;
std::mutex mapMutex;
std::mutex setMutex;

extern "C" {
  void CreateMap(const char* id) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = maps.find(id);
    if (it == maps.end()) {
      maps[id] = std::move(FlatMap());
    }
    else {
      it->second.clear();
    }
  }

  void CreateSet(const char* id) {
    std::lock_guard<std::mutex> lock(setMutex);
    auto it = sets.find(id);
    if (it == sets.end()) {
      sets[id] = std::move(FlatSet());
    }
    else {
      it->second.clear();
    }
  }

  bool TryAddDoubleToMap(const char* id, const char* key, double value) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = maps.find(id);
    if (it != maps.end()) {
      auto result = it->second.insert_or_assign(key, value);
      return result.second;
    }
    return false;
  }

  bool TryAddStringToMap(const char* id, const char* key, const char* value) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = maps.find(id);
    if (it != maps.end()) {
      auto result = it->second.insert_or_assign(key, std::string(value));
      return result.second;
    }
    return false;
  }

  bool TryAddStringToSet(const char* id, const char* key) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = sets.find(id);
    if (it != sets.end()) {
      auto result = it->second.insert(std::string(key));
      return result.second;
    }
    return false;
  }

  bool TryRemoveFromMap(const char* id, const char* key) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = maps.find(id);
    if (it != maps.end()) {
      return it->second.erase(key) > 0;
    }
    return false;
  }

  bool TryRemoveFromSet(const char* id, const char* key) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = sets.find(id);
    if (it != sets.end()) {
      return it->second.erase(key) > 0;
    }
    return false;
  }

  bool IsInMap(const char* id, const char* key) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = maps.find(id);
    if (it != maps.end()) {
      return it->second.contains(key);
    }
    return false;
  }

  bool IsInSet(const char* id, const char* key) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = sets.find(id);
    if (it != sets.end()) {
      return it->second.contains(key);
    }
    return false;
  }

  long GetMapSize(const char* id) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto it = maps.find(id);
    if (it != maps.end()) {
      return it->second.size();
    }
    return 0;
  }

  long GetSetSize(const char* id) {
    std::lock_guard<std::mutex> lock(setMutex);
    auto it = sets.find(id);
    if (it != sets.end()) {
      return it->second.size();
    }
    return 0;
  }

  const char* GetStringMapValue(const char* id, const char* key) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto mapIt = maps.find(id);
    if (mapIt != maps.end()) {
      auto& theMap = mapIt->second;
      auto it = theMap.find(key);
      if (it != theMap.end()) {
        return std::get<std::string>(it->second).c_str();
      }
    }
    return nullptr;
  }

  double GetDoubleMapValue(const char* id, const char* key) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto mapIt = maps.find(id);
    if (mapIt != maps.end()) {
      auto& theMap = mapIt->second;
      auto it = theMap.find(key);
      if (it != theMap.end()) {
        return std::get<double>(it->second);
      }
    }
    return NULL_DOUBLE;
  }

  DoubleKeyValuePair* GetDoubleMapEntries(const char* id, int* size) {
    std::lock_guard<std::mutex> lock(mapMutex);
    auto mapIt = maps.find(id);
    if (mapIt != maps.end()) {
      auto& theMap = mapIt->second;
      int count = theMap.size();
      *size = count;
      DoubleKeyValuePair* entries = static_cast<DoubleKeyValuePair*>(CoTaskMemAlloc(count * sizeof(DoubleKeyValuePair)));
      int index = 0;
      for (const auto& item : theMap) {
        size_t len = item.first.size() + 1;
        entries[index].key = static_cast<char*>(CoTaskMemAlloc(len));
        strcpy_s(entries[index].key, len, item.first.c_str());
        entries[index].value = std::get<double>(item.second);
        ++index;
      }
      return entries;
    }
    *size = 0;
    return nullptr;
  }
}