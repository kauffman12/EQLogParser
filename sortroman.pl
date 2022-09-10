file = open('procs2.txt', 'r')
for line in file:
  if ' ' not in line:
    print (line)
