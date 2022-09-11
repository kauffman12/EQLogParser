# Used to generate the spell data included with the parser

import os.path

DBSpellsFile = 'spells_us.txt'
DBSpellsStrFile = 'spells_us_str.txt'

ADPS_CASTER_VALUE = 1
ADPS_MELEE_VALUE = 2
ADPS_TANK_VALUE = 4
ADPS_ALL_VALUE = ADPS_CASTER_VALUE + ADPS_MELEE_VALUE + ADPS_TANK_VALUE
ADPS_CASTER = [ 8, 9, 15, 97, 118, 124, 127, 132, 170, 212, 273, 286, 294, 302, 303, 339, 351, 358, 374, 375, 383, 399, 413, 461, 462, 501, 507 ]
ADPS_MELEE = [ 2, 4, 5, 11, 118, 119, 169, 171, 176, 177, 182, 184, 185, 186, 189, 190, 198, 200, 201, 216, 220, 225, 227, 250, 252, 258, 266, 276, 279, 280, 301, 330, 339, 351, 364, 374, 383, 418, 427, 429, 433, 459, 471, 473, 482, 496, 498, 499, 503 ]
ADPS_TANK = [ 1, 6, 7, 55, 114, 120, 125, 161, 162, 163, 168, 174, 175, 178, 181, 188, 197, 213, 214, 214, 259, 320, 323, 393, 405, 450, 451, 452, 505, 515, 516 ]
ADPS_LIST = ADPS_CASTER + ADPS_MELEE + ADPS_TANK
ADPS_B1_MIN = { 11: 100 }
ADPS_B1_MAX = { 182: 0 }
IGNORE = [ 'Test Shield', 'SKU', 'SummonTest', ' Test', 'test atk', 'PvPS', 'BetaTestSpell', 'AA_SPELL_PH', 'test speed', ' test', 'Beta ', 'GM ', 'BetaAcrylia', 'NA ', 'MRC -', '- RESERVED', 'N/A', 'SKU27', 'Placeholder', 'Type3', 'Type 3', 'AVCReserved', ' ID Focus ', 'Use Ability', 'Beta Fish' ]
IS_TARGETRING = [ 'Issuance' ]

RANKS = [ '1', '2', '3', '4', '5', '6', '7', '8', '9', 'Third', 'Fifth', 'Octave', 'Azia', 'Beza', 'Caza' ]
ROMAN = [ (400, 'CD'), (100, 'C'), (90, 'XC'), (50, 'L'), (40, 'XL'), (10, 'X'), (9, 'IX'), (5, 'V'), (4, 'IV'), (1, 'I') ]

ADPS_EXT_DUR = dict()
DBSTRINGS = dict()
MAX_HITS = dict()

# bard
ADPS_EXT_DUR[4516] = 1    # Improved Deftdance Disc
ADPS_EXT_DUR[8030] = 2    # Improved Thousand Blades Disc
# beast
ADPS_EXT_DUR[4671] = 1    # Improved Protective Spirit Disc
# berserker
ADPS_EXT_DUR[8003] = 90   # Extended Havoc
ADPS_EXT_DUR[36556] = 90
ADPS_EXT_DUR[36557] = 90
ADPS_EXT_DUR[36558] = 90
ADPS_EXT_DUR[5041] = 3    # Improved Berserking Disc
ADPS_EXT_DUR[10923] = 3
ADPS_EXT_DUR[10924] = 3
ADPS_EXT_DUR[10925] = 3
ADPS_EXT_DUR[14189] = 3
ADPS_EXT_DUR[14190] = 3
ADPS_EXT_DUR[14191] = 3
ADPS_EXT_DUR[30463] = 3
ADPS_EXT_DUR[30464] = 3
ADPS_EXT_DUR[30465] = 3
ADPS_EXT_DUR[36529] = 3
ADPS_EXT_DUR[36530] = 3
ADPS_EXT_DUR[36531] = 3
ADPS_EXT_DUR[27257] = 2   # Improved Cleaning Acrimony Disc
ADPS_EXT_DUR[27258] = 2
ADPS_EXT_DUR[27259] = 2
#ranger
ADPS_EXT_DUR[4506] = 20   # Improved Trueshot Disc
ADPS_EXT_DUR[15091] = 20
ADPS_EXT_DUR[15092] = 20
ADPS_EXT_DUR[15093] = 20
ADPS_EXT_DUR[19223] = 20
ADPS_EXT_DUR[19224] = 20
ADPS_EXT_DUR[19225] = 20
ADPS_EXT_DUR[25525] = 20
ADPS_EXT_DUR[25526] = 20
ADPS_EXT_DUR[25527] = 20
ADPS_EXT_DUR[40123] = 20
ADPS_EXT_DUR[40124] = 20
ADPS_EXT_DUR[40125] = 20
# rogue
ADPS_EXT_DUR[35333] = 6   # Extended Aspbleeder Disc
ADPS_EXT_DUR[35334] = 6 
ADPS_EXT_DUR[35335] = 6 
ADPS_EXT_DUR[44169] = 6 
ADPS_EXT_DUR[44170] = 6
ADPS_EXT_DUR[44171] = 6
ADPS_EXT_DUR[56321] = 6
ADPS_EXT_DUR[56322] = 6
ADPS_EXT_DUR[56323] = 6
ADPS_EXT_DUR[59643] = 6
ADPS_EXT_DUR[59644] = 6
ADPS_EXT_DUR[59645] = 6
ADPS_EXT_DUR[63150] = 6
ADPS_EXT_DUR[63151] = 6
ADPS_EXT_DUR[63152] = 6
ADPS_EXT_DUR[35327] = 15  # Improved Fatal Aim Disc
ADPS_EXT_DUR[35328] = 15
ADPS_EXT_DUR[35329] = 15
ADPS_EXT_DUR[56288] = 15 
ADPS_EXT_DUR[56289] = 15
ADPS_EXT_DUR[56290] = 15
ADPS_EXT_DUR[6197] = 2    # Improved Frenzied Stabbing Disc
ADPS_EXT_DUR[8001] = 90   # Improved Thief's Eyes
ADPS_EXT_DUR[40294] = 90
ADPS_EXT_DUR[40295] = 90
ADPS_EXT_DUR[40296] = 90
ADPS_EXT_DUR[4695] = 5    # Improved Twisted Chance Disc
# monk
ADPS_EXT_DUR[14820] = 3   # Extended Crystalpalm Discipline 
ADPS_EXT_DUR[14821] = 3
ADPS_EXT_DUR[14822] = 3
ADPS_EXT_DUR[18925] = 3
ADPS_EXT_DUR[18926] = 3
ADPS_EXT_DUR[18927] = 3
ADPS_EXT_DUR[29030] = 3
ADPS_EXT_DUR[29031] = 3
ADPS_EXT_DUR[29032] = 3
ADPS_EXT_DUR[35071] = 3
ADPS_EXT_DUR[35072] = 3
ADPS_EXT_DUR[35073] = 3
ADPS_EXT_DUR[8473] = 3    # Extended Heel of Kanji Disc
ADPS_EXT_DUR[25941] = 3
ADPS_EXT_DUR[25942] = 3
ADPS_EXT_DUR[25943] = 3
ADPS_EXT_DUR[29048] = 3
ADPS_EXT_DUR[29049] = 3
ADPS_EXT_DUR[29050] = 3
ADPS_EXT_DUR[35086] = 3
ADPS_EXT_DUR[35087] = 3
ADPS_EXT_DUR[35088] = 3
ADPS_EXT_DUR[10938] = 2   # Extended Impenetrable Disc
ADPS_EXT_DUR[10939] = 2
ADPS_EXT_DUR[10940] = 2
ADPS_EXT_DUR[10941] = 2
ADPS_EXT_DUR[10942] = 2
ADPS_EXT_DUR[10943] = 2
ADPS_EXT_DUR[4690] = 2
ADPS_EXT_DUR[35089] = 2
ADPS_EXT_DUR[35090] = 2
ADPS_EXT_DUR[35091] = 2
ADPS_EXT_DUR[11922] = 2   # Improved Scaledfist Disc
ADPS_EXT_DUR[11923] = 2
ADPS_EXT_DUR[11924] = 2
ADPS_EXT_DUR[25923] = 2
ADPS_EXT_DUR[25924] = 2
ADPS_EXT_DUR[25925] = 2
ADPS_EXT_DUR[4691] = 3    # Improved Speed Focus Disc
# sk/paladin
ADPS_EXT_DUR[58778] = 6   # Enduring Reproval
ADPS_EXT_DUR[58779] = 6
ADPS_EXT_DUR[58780] = 6
ADPS_EXT_DUR[55317] = 6
ADPS_EXT_DUR[55318] = 6
ADPS_EXT_DUR[55319] = 6
ADPS_EXT_DUR[43286] = 6
ADPS_EXT_DUR[43287] = 6
ADPS_EXT_DUR[43288] = 6
ADPS_EXT_DUR[59214] = 20  # Extended Decrepit Skin
ADPS_EXT_DUR[59215] = 20
ADPS_EXT_DUR[59216] = 20
ADPS_EXT_DUR[55798] = 20
ADPS_EXT_DUR[55799] = 20
ADPS_EXT_DUR[55800] = 20
ADPS_EXT_DUR[43695] = 20
ADPS_EXT_DUR[43696] = 20
ADPS_EXT_DUR[43697] = 20
ADPS_EXT_DUR[62713] = 20
ADPS_EXT_DUR[62714] = 20
ADPS_EXT_DUR[62715] = 20
ADPS_EXT_DUR[58781] = 30  # Extended Steely Stance
ADPS_EXT_DUR[58782] = 30 
ADPS_EXT_DUR[58783] = 30
ADPS_EXT_DUR[55320] = 30
ADPS_EXT_DUR[55321] = 30
ADPS_EXT_DUR[55322] = 30
ADPS_EXT_DUR[43289] = 30
ADPS_EXT_DUR[43290] = 30
ADPS_EXT_DUR[43291] = 30
ADPS_EXT_DUR[62293] = 30
ADPS_EXT_DUR[62294] = 30
ADPS_EXT_DUR[62295] = 30
ADPS_EXT_DUR[58898] = 20  # Extended Preservation of Marr 
ADPS_EXT_DUR[58899] = 20
ADPS_EXT_DUR[58900] = 20
ADPS_EXT_DUR[55458] = 20
ADPS_EXT_DUR[55459] = 20
ADPS_EXT_DUR[55460] = 20
ADPS_EXT_DUR[34461] = 20
ADPS_EXT_DUR[34462] = 20
ADPS_EXT_DUR[34463] = 20
ADPS_EXT_DUR[62431] = 20
ADPS_EXT_DUR[62432] = 20
ADPS_EXT_DUR[62433] = 20
# war
ADPS_EXT_DUR[58557] = 98  # Extended Bracing Defense
ADPS_EXT_DUR[58558] = 98
ADPS_EXT_DUR[58559] = 98
ADPS_EXT_DUR[55057] = 98
ADPS_EXT_DUR[55058] = 98
ADPS_EXT_DUR[55059] = 98
ADPS_EXT_DUR[43060] = 98
ADPS_EXT_DUR[43061] = 98
ADPS_EXT_DUR[43062] = 98
ADPS_EXT_DUR[62060] = 98
ADPS_EXT_DUR[62061] = 98
ADPS_EXT_DUR[62062] = 98
ADPS_EXT_DUR[8000] = 90  # Commanding Voice
ADPS_EXT_DUR[58554] = 104 # Extended Field Armorer
ADPS_EXT_DUR[58555] = 104
ADPS_EXT_DUR[58556] = 104
ADPS_EXT_DUR[55054] = 104
ADPS_EXT_DUR[55055] = 104
ADPS_EXT_DUR[55056] = 104
ADPS_EXT_DUR[43057] = 104
ADPS_EXT_DUR[43058] = 104
ADPS_EXT_DUR[43059] = 104
ADPS_EXT_DUR[62057] = 104
ADPS_EXT_DUR[62058] = 104
ADPS_EXT_DUR[62059] = 104
ADPS_EXT_DUR[15369] = 1  # Extended Shield Reflect
ADPS_EXT_DUR[15370] = 1
ADPS_EXT_DUR[15371] = 1

# sk/paladin
MAX_HITS[58778] = 3   # Enduring Reproval
MAX_HITS[58779] = 3
MAX_HITS[58780] = 3
MAX_HITS[55317] = 3
MAX_HITS[55318] = 3
MAX_HITS[55319] = 3
MAX_HITS[43286] = 3
MAX_HITS[43287] = 3
MAX_HITS[43288] = 3
MAX_HITS[59214] = 68  # Extended Decrepit Skin
MAX_HITS[59215] = 68
MAX_HITS[59216] = 68
MAX_HITS[55798] = 68
MAX_HITS[55799] = 68
MAX_HITS[55800] = 68
MAX_HITS[43695] = 68
MAX_HITS[43696] = 68
MAX_HITS[43697] = 68
MAX_HITS[62713] = 68
MAX_HITS[62714] = 68
MAX_HITS[62715] = 68
MAX_HITS[58898] = 68  # Extended Preservation of Marr
MAX_HITS[58899] = 68
MAX_HITS[58900] = 68
MAX_HITS[55458] = 68
MAX_HITS[55459] = 68
MAX_HITS[55460] = 68
MAX_HITS[34461] = 68
MAX_HITS[34462] = 68
MAX_HITS[34463] = 68

def abbreviate(name):
  result = name
  rankIndex = name.find(' Rk.')
  if rankIndex > -1:
    result = name[0:rankIndex]
  else:
    lastSpace = name.rfind(' ')
    if lastSpace > -1:
      hasRank = True
      test = name[lastSpace+1:]
      if test not in RANKS:
        hasRank = False
      if hasRank:
        result = name[0:lastSpace]
        if test in ['Octave', 'Fifth', 'Third']:
          result = result + ' Root' 
  return result
  
def getAdpsValueFromSpa(current, spa):
  if current == ADPS_ALL_VALUE:
    return current
  updated = 0
  if spa in ADPS_CASTER:
    updated = ADPS_CASTER_VALUE
  if spa in ADPS_MELEE:
    updated = updated + ADPS_MELEE_VALUE
  if spa in ADPS_TANK:
    updated = updated + ADPS_TANK_VALUE
  if current > 0:
    updated = updated | current 
  return updated

def getAdpsValueFromSkill(current, skill, endurance):
  if current == ADPS_ALL_VALUE:
    return current
  updated = 0
  if skill == 15 and endurance > 0:
    updated = updated + ADPS_TANK_VALUE
  if current > 0:
    updated = updated | current 
  return updated
  
def isTargetRing(name):
  for test in IS_TARGETRING:
    if name.startswith(test):
      return True
  return False
  
def intToRoman(number):
  result = ''
  for (arabic, roman) in ROMAN:
    (factor, number) = divmod(number, arabic)
    result += roman * factor
  return result

# load RANKS
for number in range(1, 200):
  RANKS.append(intToRoman(number))  

# DB strings for lands on messages
if os.path.isfile(DBSpellsStrFile):
  print('Loading Spell Strings from %s' % DBSpellsStrFile)
  db = open(DBSpellsStrFile, 'r')
  for line in db:
    data = line.split('^')

    try:
      id = data[0]
      landOnYou = data[3]
      landOnOther = data[4]
      wearOff = data[5]
      DBSTRINGS[id] = { 'landsOnYou': landOnYou, 'landsOnOther': landOnOther, 'wearOff': wearOff }
    except ValueError:
      pass

# parse the spell file
if os.path.isfile(DBSpellsFile):
  print('Loading Spells DB from %s' % DBSpellsFile)

  spells = dict()
  test = dict()
  recourses = dict()
  spa339s = dict()
  spa340s = dict()
  spa373s = dict()
  spa374s = dict()
  spa406s = dict()

  for line in open(DBSpellsFile, 'r'):
    procs = []
    data = line.split('^')
    id = data[0]
    intId = int(id)
    name = data[1]

    if len(name) <= 3:
      continue

    skip = False
    for ig in IGNORE:
      if ig in name:
        skip = True
       
    if skip:
      continue

    spellRange = int(data[4])
    castTime = int(data[8])
    lockoutTime = int(data[9])
    recastTime = int(data[10])
    maxDuration = int(data[12])
    manaCost = int(data[14])
    beneficial = int(data[28])
    resist = int(data[29])
    spellTarget = int(data[30])
    skill = int(data[32])
    recourse = data[81]
    songWindow = int(data[84])
    reflectable = int(data[91])
    hateMod = int(data[92])
    endurance = int(data[96])
    combatSkill = int(data[98])
    hateOver = int(data[99])
    maxHits = int(data[102])
    mgb = int(data[110])
    dispellable = int(data[111])
    focusable = int(data[122]) # focusable
    blockable = int(data[130])
    rank = int(data[133]) # AA rank
    origDuration = maxDuration

    if isTargetRing(name):
      spellTarget = 45  

    # add focus AAs for additional hits
    if intId in MAX_HITS:
      maxHits = maxHits + MAX_HITS[intId]
    
    # ignore long term beneficial buffs like FIRE DAMAGE
    # howerver allow their SPAs to be checked for procs so continue at the end
    if origDuration == 1950 and castTime == 0 and lockoutTime == 0 and recastTime == 0 and beneficial != 0:
      continue
	  
    classMask = 0
    minLevel = 255
    for i in range(36, 36+16):
      level = int(data[i])
      if level <= 254:
        classMask += (1 << (i - 36))
        minLevel = min(minLevel, level)

    if origDuration == 1950:
      classMask = 65535

    adps = getAdpsValueFromSkill(0, skill, endurance)
    damaging = 0
    charm = False
    
    for slot in data[-1].split('$'):
      values = slot.split('|')
      if len(values) > 1:
        spa = int(values[1])
        base1 = values[2]
        base2 = values[3]
        
        if spa == 22:
          charm = True

        if spa == 0 or spa == 79 or spa == 100:
          if int(base1) > 0:
            damaging = -1
          else:
            damaging = 1
          if int(base1) <= -50000000:
            damaging = 2 # BANE
            
        if spa in ADPS_LIST:
          if spa in ADPS_B1_MIN:
            if int(base1) >= ADPS_B1_MIN[spa]:
              adps = getAdpsValueFromSpa(adps, spa)
          elif spa in ADPS_B1_MAX:
            if int(base1) < ADPS_B1_MAX[spa]:
              adps = getAdpsValueFromSpa(adps, spa)
          elif int(base1) >= 0:
            adps = getAdpsValueFromSpa(adps, spa)           
            
        if spa == 339 and int(base2) > 0:
          spa339s[base2] = id
          procs.append(base2)
        elif spa == 340 and int(base2) > 0:
          spa340s[base2] = id
          procs.append(base2)
        elif spa == 373 and int(base2) > 0:
          spa373s[base1] = id 
          procs.append(base1)
        elif spa == 374 and int(base2) > 0:
          spa374s[base2] = id 
          procs.append(base2)
        elif spa == 406 and int(base1) > 0:
          spa406s[base1] = id
          procs.append(base1)
        elif spa == 411 and classMask == 0:
          classMask = (int(base1) >> 1)
		  
    # apply 100% buff extension
    if beneficial != 0 and focusable == 0 and combatSkill == 0 and maxDuration > 1:
      maxDuration = maxDuration * 2

    # add focus AAs that extend duration
    if intId in ADPS_EXT_DUR:
      maxDuration = maxDuration + ADPS_EXT_DUR[intId]
	  
    info = dict()
    if int(recourse) > 0:
      recourses[recourse] = id
      procs.append(recourse)

    # filter some obvious non-player adps
    if charm and adps != 0:
      adps = 0

    abbrv = abbreviate(name)
    info['abbrv'] = abbrv
    info['adps'] = adps
    info['castTime'] = castTime
    info['damaging'] = damaging
    info['id'] = id
    info['intId'] = intId
    info['beneficial'] = beneficial
    info['blockable'] = blockable
    info['combatSkill'] = combatSkill
    info['classMask'] = classMask
    info['dispellable'] = dispellable
    info['focusable'] = focusable
    info['hateMod'] = hateMod
    info['hateOver'] = hateOver
    info['lockoutTime'] = lockoutTime
    info['manaCost'] = manaCost
    info['maxDuration'] = maxDuration
    info['mgb'] = mgb
    info['origDuration'] = origDuration
    info['maxHits'] = maxHits
    info['name'] = name
    info['procs'] = procs
    info['resist'] = resist
    info['skill'] = skill
    info['rank'] = rank
    info['level'] = minLevel
    info['recastTime'] = recastTime
    info['spellTarget'] = spellTarget
    info['spellRange'] = spellRange
    info['songWindow'] = songWindow

    if id in DBSTRINGS:
      info['landsOnYou'] = DBSTRINGS[id]['landsOnYou']
      info['landsOnOther'] = DBSTRINGS[id]['landsOnOther']
      info['wearOff'] = DBSTRINGS[id]['wearOff']

    # Overdrive Punch the proc and main spell have the same name. Just ignore the non-damaging versions
    if name != 'Overdrive Punch' or beneficial == 1:
      spells[id] = info

  for i in range(6):
    for id in spells:
      if id in recourses and spells[recourses[id]]['classMask'] > 0:
        spells[id]['classMask'] |= spells[recourses[id]]['classMask']
      if id in spa339s and spells[spa339s[id]]['classMask'] > 0:
        spells[id]['classMask'] |= spells[spa339s[id]]['classMask']
      if id in spa340s and spells[spa340s[id]]['classMask'] > 0:
        spells[id]['classMask'] |= spells[spa340s[id]]['classMask']
      if id in spa373s and spells[spa373s[id]]['classMask'] > 0:
        spells[id]['classMask'] |= spells[spa373s[id]]['classMask']
      if id in spa374s and spells[spa374s[id]]['classMask'] > 0:
        spells[id]['classMask'] |= spells[spa374s[id]]['classMask']
      if id in spa406s and spells[spa406s[id]]['classMask'] > 0:
        spells[id]['classMask'] |= spells[spa406s[id]]['classMask']
      if spells[id]['classMask'] > 0:
        for proc in spells[id]['procs']:
          if spells[proc]['classMask'] == 0:
            spells[proc]['classMask'] |= spells[id]['classMask']

  for id in spells:
    name = spells[id]['name']
    if name in test:
      if test[name][0]['origDuration'] != spells[id]['origDuration'] or test[name][0]['focusable'] != spells[id]['focusable']:
        test[name].append(spells[id])
    else:
      test[name] = [spells[id]]

  final = dict()
  for k in test:
    newest = None
    for info in test[k]:
      unique = False
      if newest == None:
        newest = info
      if info['classMask'] > 0 or info['rank'] > 0:
        unique = True
      if info['hateOver'] > 0 or info['hateMod'] > 0:
        unique = True
      if info['spellTarget'] == 6 and info['beneficial'] != 0 and info['origDuration'] == 600 and info['blockable'] == 0:
        unique = True
      if info['beneficial'] != 0 and info['spellTarget'] == 14:
        unique = True
      if 'landsOnOther' in info and info['landsOnOther'] != "":
        unique = True

      if unique:
        if newest['intId'] < info['intId']:
          newest = info
        continue

      final[info['intId']] = info
    final[newest['intId']] = newest

  output = open('output.txt', 'w')
  for key in sorted(final):
    data = '%s^%s^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%s^%s^%s' % (final[key]['intId'], final[key]['name'], final[key]['level'], final[key]['maxDuration'], final[key]['beneficial'], final[key]['maxHits'], final[key]['spellTarget'], final[key]['classMask'], final[key]['damaging'], final[key]['combatSkill'], final[key]['resist'], final[key]['songWindow'], final[key]['adps'], final[key]['mgb'], final[key]['rank'], final[key]['landsOnYou'], final[key]['landsOnOther'], final[key]['wearOff'])
    output.write(data)
    output.write('\n')
  output.write('900001^Glyph of Destruction I^254^20^1^0^6^65407^0^0^0^0^3^0^1^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
  output.write('900002^Glyph of Destruction II^254^20^1^0^6^65407^0^0^0^0^3^0^2^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
  output.write('900003^Glyph of Destruction III^254^20^1^0^6^65407^0^0^0^0^3^0^3^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
  output.write('900004^Glyph of Destruction IV^254^20^1^0^6^65407^0^0^0^0^3^0^4^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
  output.write('900005^Glyph of Destruction V^254^20^1^0^6^65407^0^0^0^0^3^0^5^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
