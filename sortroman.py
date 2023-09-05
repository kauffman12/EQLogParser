import math
import functools

TO_INT = { "I": 1, "V": 5, "X": 10, "L": 50, "C": 100, "D": 500 }
SUB = { 'IV': 4, 'IX':9, 'XL': 40, 'XC': 90, 'CD':400, 'CM': 900 }

def rom_to_int(s:str):
  summation = 0
  idx = 0
  while idx < len(s):
    if s[idx:idx+2] in SUB:
      summation += SUB.get(s[idx:idx+2])
      idx += 2
    elif s[idx] in TO_INT:
      summation += TO_INT.get(s[idx])
      idx += 1
    else:
      return -1
  return summation

def to_roman(num):
  m = ["", "M", "MM", "MMM"]
  c = ["", "C", "CC", "CCC", "CD", "D", "DC", "DCC", "DCCC", "CM "]
  x = ["", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC"]
  i = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"]
  
  # Converting to roman
  thousands = m[num // 1000]
  hundreds = c[(num % 1000) // 100]
  tens = x[(num % 100) // 10]
  ones = i[num % 10]
 
  ans = (thousands + hundreds + tens + ones)
  return ans

def compare(a, b):
  aSplit = a.split()
  bSplit = b.split()
  if len(aSplit) == len(bSplit):
    if aSplit[-1].isnumeric() and bSplit[-1].isnumeric():
      for i in range(len(aSplit) - 1):
        if aSplit[i] != bSplit[i]:
          if aSplit[i] < bSplit[i]:
            return -1
          if aSplit[i] > bSplit[i]:
            return 1    
      aInt = int(aSplit[-1])
      bInt = int(bSplit[-1])
      if aInt < bInt:
        return -1
      if aInt == bInt:
        return 0
      return 1
  if a < b:
    return -1
  if a == b:
    return 0
  return 1

procs = open('EQLogParser/data/procs.txt', 'r')

groups = []
current = None
dont = dict()
for proc in procs:
  if proc != "":
    if proc[0] == "#":
      current = { 'data': [], 'title': proc.strip() }
      groups.append(current)
      continue
    else:
      split = proc.split()
      convert = False
      if len(split) > 1:
        roman = True
        for c in split[-1]:
          if c not in ['I', 'V', 'X', 'L', 'C']:
            roman = False
        convert = roman
      if convert:
        current['data'].append(' '.join(split[0:-1]) + ' ' + str(rom_to_int(split[-1])))
      else:
        current['data'].append(proc.strip())
        dont[proc.strip()] = True

output = open('sorted.txt', 'w')
for group in groups:
  output.write(group['title'])
  for d in sorted(group['data'], key=functools.cmp_to_key(compare)):
    if d not in dont:
      split = d.split()
      if len(split) > 1 and split[-1].isnumeric():
        output.write(' '.join(split[0:-1]) + ' ' + to_roman(int(split[-1])))
      else:
        output.write(d)
    else:
      output.write(d)
    output.write('\n')

  if group != groups[-1]:
    output.write('\n')
