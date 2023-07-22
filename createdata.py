# Used to generate the spell data included with the parser

import os.path

DBSpellsFile = 'spells_us.txt'
DBSpellsStrFile = 'spells_us_str.txt'

ADPS_CASTER_VALUE = 1
ADPS_MELEE_VALUE = 2
ADPS_TANK_VALUE = 4
ADPS_HEALER_VALUE = 8
ADPS_ALL_VALUE = ADPS_CASTER_VALUE + ADPS_MELEE_VALUE + ADPS_TANK_VALUE + ADPS_HEALER_VALUE
ADPS_CASTER = [ 8, 9, 15, 97, 118, 124, 127, 132, 170, 212, 273, 286, 294, 302, 303, 339, 351, 358, 374, 375, 383, 399, 413, 461, 462, 501, 507 ]
ADPS_MELEE = [ 2, 4, 5, 11, 118, 119, 169, 171, 176, 177, 182, 184, 185, 186, 189, 190, 198, 200, 201, 216, 220, 225, 227, 250, 252, 258, 266, 276, 279, 280, 301, 330, 339, 351, 364, 374, 383, 418, 427, 429, 433, 459, 471, 473, 482, 496, 498, 499, 503 ]
ADPS_TANK = [ 1, 6, 7, 55, 114, 120, 125, 147, 161, 162, 163, 168, 172, 173, 174, 175, 178, 181, 188, 197, 213, 214, 214, 259, 320, 323, 393, 405, 450, 451, 452, 505, 515, 516 ]
ADPS_HEALER = [ 9, 15, 44, 97, 120, 125, 127, 132, 274, 392, 394, 395, 396, 399, 400, 501 ]
ADPS_LIST = ADPS_CASTER + ADPS_MELEE + ADPS_TANK + ADPS_HEALER
ADPS_B1_MIN = { 4: 1, 5: 1, 6: 1, 7: 1, 8: 1, 9: 1, 11: 100 }
ADPS_B1_MAX = { 182: 0, 197: 0 }
ADPS_BEN_DET = [ 399 ]
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
ADPS_EXT_DUR[5041] = 5    # Improved Berserking Disc
ADPS_EXT_DUR[10923] = 5 
ADPS_EXT_DUR[10924] = 5 
ADPS_EXT_DUR[10925] = 5 
ADPS_EXT_DUR[14189] = 5
ADPS_EXT_DUR[14190] = 5
ADPS_EXT_DUR[14191] = 5
ADPS_EXT_DUR[30463] = 5
ADPS_EXT_DUR[30464] = 5
ADPS_EXT_DUR[30465] = 5
ADPS_EXT_DUR[36529] = 5
ADPS_EXT_DUR[36530] = 5
ADPS_EXT_DUR[36531] = 5
ADPS_EXT_DUR[27257] = 2   # Improved Cleaving Acrimony Disc
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
ADPS_EXT_DUR[63114] = 15
ADPS_EXT_DUR[63115] = 15
ADPS_EXT_DUR[63116] = 15
ADPS_EXT_DUR[6197] = 2    # Improved Frenzied Stabbing Disc
ADPS_EXT_DUR[8001] = 90   # Improved Thief's Eyes
ADPS_EXT_DUR[40294] = 90
ADPS_EXT_DUR[40295] = 90
ADPS_EXT_DUR[40296] = 90
ADPS_EXT_DUR[65357] = 90
ADPS_EXT_DUR[65358] = 90
ADPS_EXT_DUR[65359] = 90
ADPS_EXT_DUR[4695] = 5    # Improved Twisted Chance Disc
# monk
ADPS_EXT_DUR[14820] = 3   # Improved Crystalpalm Discipline 
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
ADPS_EXT_DUR[8473] = 3    # Improved Heel of Kanji Disc
ADPS_EXT_DUR[25941] = 3
ADPS_EXT_DUR[25942] = 3
ADPS_EXT_DUR[25943] = 3
ADPS_EXT_DUR[29048] = 3
ADPS_EXT_DUR[29049] = 3
ADPS_EXT_DUR[29050] = 3
ADPS_EXT_DUR[35086] = 3
ADPS_EXT_DUR[35087] = 3
ADPS_EXT_DUR[35088] = 3
ADPS_EXT_DUR[10938] = 5   # Extended Impenetrable Disc
ADPS_EXT_DUR[10939] = 5 
ADPS_EXT_DUR[10940] = 5
ADPS_EXT_DUR[10941] = 5
ADPS_EXT_DUR[10942] = 5
ADPS_EXT_DUR[10943] = 5
ADPS_EXT_DUR[4690] = 5
ADPS_EXT_DUR[35089] = 5
ADPS_EXT_DUR[35090] = 5
ADPS_EXT_DUR[35091] = 5
ADPS_EXT_DUR[11922] = 2   # Improved Scaledfist Disc
ADPS_EXT_DUR[11923] = 2
ADPS_EXT_DUR[11924] = 2
ADPS_EXT_DUR[25923] = 2
ADPS_EXT_DUR[25924] = 2
ADPS_EXT_DUR[25925] = 2
ADPS_EXT_DUR[4691] = 3    # Improved Speed Focus Disc
# sk/paladin
ADPS_EXT_DUR[19134] = 2  # Enduring Reproval
ADPS_EXT_DUR[19135] = 2 
ADPS_EXT_DUR[19136] = 2
ADPS_EXT_DUR[25267] = 2
ADPS_EXT_DUR[25268] = 2
ADPS_EXT_DUR[25269] = 2
ADPS_EXT_DUR[28311] = 2
ADPS_EXT_DUR[28312] = 2
ADPS_EXT_DUR[28313] = 2
ADPS_EXT_DUR[34317] = 2
ADPS_EXT_DUR[34318] = 2
ADPS_EXT_DUR[34319] = 2
ADPS_EXT_DUR[43286] = 2
ADPS_EXT_DUR[43287] = 2
ADPS_EXT_DUR[43288] = 2
ADPS_EXT_DUR[55317] = 2
ADPS_EXT_DUR[55318] = 2
ADPS_EXT_DUR[55319] = 2
ADPS_EXT_DUR[58778] = 2
ADPS_EXT_DUR[58779] = 2
ADPS_EXT_DUR[58780] = 2
ADPS_EXT_DUR[58780] = 2
ADPS_EXT_DUR[62290] = 2
ADPS_EXT_DUR[62291] = 2
ADPS_EXT_DUR[62292] = 2
ADPS_EXT_DUR[28754] = 20 # Extended Decrepit Skin
ADPS_EXT_DUR[28755] = 20
ADPS_EXT_DUR[28756] = 20
ADPS_EXT_DUR[34769] = 20
ADPS_EXT_DUR[34770] = 20
ADPS_EXT_DUR[34771] = 20
ADPS_EXT_DUR[43695] = 20
ADPS_EXT_DUR[43696] = 20
ADPS_EXT_DUR[43697] = 20
ADPS_EXT_DUR[55798] = 20
ADPS_EXT_DUR[55799] = 20
ADPS_EXT_DUR[55800] = 20
ADPS_EXT_DUR[59214] = 20 
ADPS_EXT_DUR[59215] = 20
ADPS_EXT_DUR[59216] = 20
ADPS_EXT_DUR[62713] = 20
ADPS_EXT_DUR[62714] = 20
ADPS_EXT_DUR[62715] = 20
ADPS_EXT_DUR[19137] = 30  # Extended Steely Stance
ADPS_EXT_DUR[19138] = 30
ADPS_EXT_DUR[19139] = 30
ADPS_EXT_DUR[25270] = 30
ADPS_EXT_DUR[25271] = 30
ADPS_EXT_DUR[25272] = 30
ADPS_EXT_DUR[28314] = 30
ADPS_EXT_DUR[28315] = 30
ADPS_EXT_DUR[28316] = 30
ADPS_EXT_DUR[34321] = 30
ADPS_EXT_DUR[34322] = 30
ADPS_EXT_DUR[34320] = 30
ADPS_EXT_DUR[34321] = 30
ADPS_EXT_DUR[34322] = 30
ADPS_EXT_DUR[43289] = 30
ADPS_EXT_DUR[43290] = 30
ADPS_EXT_DUR[43291] = 30
ADPS_EXT_DUR[55320] = 30
ADPS_EXT_DUR[55321] = 30
ADPS_EXT_DUR[55322] = 30
ADPS_EXT_DUR[58781] = 30
ADPS_EXT_DUR[58782] = 30 
ADPS_EXT_DUR[58783] = 30
ADPS_EXT_DUR[62293] = 30
ADPS_EXT_DUR[62294] = 30
ADPS_EXT_DUR[62295] = 30
ADPS_EXT_DUR[19110] = 25 # Extended Preservation of Marr
ADPS_EXT_DUR[19111] = 25
ADPS_EXT_DUR[19112] = 25
ADPS_EXT_DUR[25384] = 25
ADPS_EXT_DUR[25385] = 25
ADPS_EXT_DUR[25386] = 25
ADPS_EXT_DUR[28455] = 25
ADPS_EXT_DUR[28456] = 25
ADPS_EXT_DUR[28457] = 25
ADPS_EXT_DUR[34461] = 25
ADPS_EXT_DUR[34462] = 25
ADPS_EXT_DUR[34463] = 25
ADPS_EXT_DUR[55458] = 25
ADPS_EXT_DUR[55459] = 25
ADPS_EXT_DUR[55460] = 25
ADPS_EXT_DUR[58898] = 25
ADPS_EXT_DUR[58899] = 25
ADPS_EXT_DUR[58900] = 25
ADPS_EXT_DUR[62431] = 25
ADPS_EXT_DUR[62432] = 25
ADPS_EXT_DUR[62433] = 25
# war
ADPS_EXT_DUR[22556] = 98  # Extended Bracing Defense
ADPS_EXT_DUR[22557] = 98
ADPS_EXT_DUR[22558] = 98
ADPS_EXT_DUR[25051] = 98
ADPS_EXT_DUR[25052] = 98
ADPS_EXT_DUR[25053] = 98
ADPS_EXT_DUR[28066] = 98
ADPS_EXT_DUR[28067] = 98
ADPS_EXT_DUR[28068] = 98
ADPS_EXT_DUR[34042] = 98
ADPS_EXT_DUR[34043] = 98
ADPS_EXT_DUR[34044] = 98
ADPS_EXT_DUR[43060] = 98
ADPS_EXT_DUR[43061] = 98
ADPS_EXT_DUR[43062] = 98
ADPS_EXT_DUR[55057] = 98
ADPS_EXT_DUR[55058] = 98
ADPS_EXT_DUR[55059] = 98
ADPS_EXT_DUR[58557] = 98
ADPS_EXT_DUR[58558] = 98
ADPS_EXT_DUR[58559] = 98
ADPS_EXT_DUR[62060] = 98
ADPS_EXT_DUR[62061] = 98
ADPS_EXT_DUR[62062] = 98
ADPS_EXT_DUR[8000] = 90  # ExtendedCommanding Voice
ADPS_EXT_DUR[19917] = 104 # Extended Field Armorer
ADPS_EXT_DUR[19918] = 104
ADPS_EXT_DUR[19919] = 104
ADPS_EXT_DUR[25036] = 104
ADPS_EXT_DUR[25037] = 104
ADPS_EXT_DUR[25038] = 104
ADPS_EXT_DUR[28051] = 104
ADPS_EXT_DUR[28052] = 104
ADPS_EXT_DUR[28053] = 104
ADPS_EXT_DUR[34036] = 104
ADPS_EXT_DUR[34037] = 104
ADPS_EXT_DUR[34038] = 104
ADPS_EXT_DUR[43057] = 104
ADPS_EXT_DUR[43058] = 104
ADPS_EXT_DUR[43059] = 104
ADPS_EXT_DUR[55054] = 104
ADPS_EXT_DUR[55055] = 104
ADPS_EXT_DUR[55056] = 104
ADPS_EXT_DUR[58554] = 104
ADPS_EXT_DUR[58555] = 104
ADPS_EXT_DUR[58556] = 104
ADPS_EXT_DUR[62057] = 104
ADPS_EXT_DUR[62058] = 104
ADPS_EXT_DUR[62059] = 104
ADPS_EXT_DUR[15369] = 1  # Extended Shield Reflect
ADPS_EXT_DUR[15370] = 1
ADPS_EXT_DUR[15371] = 1

# sk/paladin
MAX_HITS[19134] = 6  # Enduring Reproval
MAX_HITS[19135] = 6 
MAX_HITS[19136] = 6
MAX_HITS[65667] = 6
MAX_HITS[65668] = 6
MAX_HITS[65669] = 6
MAX_HITS[68311] = 6
MAX_HITS[68316] = 6
MAX_HITS[68313] = 6
MAX_HITS[34317] = 6
MAX_HITS[34318] = 6
MAX_HITS[34319] = 6
MAX_HITS[43686] = 6
MAX_HITS[43687] = 6
MAX_HITS[43688] = 6
MAX_HITS[55317] = 6
MAX_HITS[55318] = 6
MAX_HITS[55319] = 6
MAX_HITS[58778] = 6
MAX_HITS[58779] = 6
MAX_HITS[58780] = 6
MAX_HITS[58780] = 6
MAX_HITS[66690] = 6
MAX_HITS[66691] = 6
MAX_HITS[66696] = 6
MAX_HITS[28754] = 68 # Extended Decrepit Skin
MAX_HITS[28755] = 68
MAX_HITS[28756] = 68
MAX_HITS[34769] = 68
MAX_HITS[34770] = 68
MAX_HITS[34771] = 68
MAX_HITS[43695] = 68
MAX_HITS[43696] = 68
MAX_HITS[43697] = 68
MAX_HITS[55798] = 68
MAX_HITS[55799] = 68
MAX_HITS[55800] = 68
MAX_HITS[59214] = 68 
MAX_HITS[59215] = 68
MAX_HITS[59216] = 68
MAX_HITS[62713] = 68
MAX_HITS[62714] = 68
MAX_HITS[62715] = 68
MAX_HITS[19110] = 83 # Extended Preservation of Marr
MAX_HITS[19111] = 83
MAX_HITS[19112] = 83
MAX_HITS[83384] = 83
MAX_HITS[83385] = 83
MAX_HITS[83386] = 83
MAX_HITS[28455] = 83
MAX_HITS[28456] = 83
MAX_HITS[28457] = 83
MAX_HITS[34461] = 83
MAX_HITS[34462] = 83
MAX_HITS[34463] = 83
MAX_HITS[55458] = 83
MAX_HITS[55459] = 83
MAX_HITS[55460] = 83
MAX_HITS[58898] = 83
MAX_HITS[58899] = 83
MAX_HITS[58900] = 83
MAX_HITS[62431] = 83
MAX_HITS[62432] = 83
MAX_HITS[62433] = 83

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
  
def getAdpsValueFromSpa(current, spa, requireDet):
  if current == ADPS_ALL_VALUE:
    return current
  updated = 0
  if spa in ADPS_CASTER:
    if spa not in ADPS_BEN_DET or (requireDet == None or requireDet == True):
      updated = ADPS_CASTER_VALUE
  if spa in ADPS_MELEE:
    updated = updated + ADPS_MELEE_VALUE
  if spa in ADPS_TANK:
    updated = updated + ADPS_TANK_VALUE
  if spa in ADPS_HEALER:
    if spa not in ADPS_BEN_DET or (requireDet == None or requireDet == False):
      updated = updated + ADPS_HEALER_VALUE
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
  byName = dict()

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

    if name in byName:
      for spell in byName[name]:
        if spell['name'] == name and spell['classMask'] != classMask:
          if classMask == 0:
            classMask = spell['classMask']
          elif spell['classMask'] == 0 and classMask > 0:
            spell['classMask'] = classMask
          
          if spell['classMask'] != classMask:
            newMask = spell['classMask'] | classMask
            spell['classMask'] = newMask
            classMask = newMask

    adps = getAdpsValueFromSkill(0, skill, endurance)
    damaging = 0
    charm = False
    requireDet = None
    
    # process in reverse order
    slots = data[-1].split('$')
    slots.reverse()

    for slot in slots:
      values = slot.split('|')
      if len(values) > 1:
        spa = int(values[1])
        base1 = values[2]
        base2 = values[3]
        
        if spa == 22:
          charm = True
        if spa == 138:
          requireDet = (base1 == '0')

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
              adps = getAdpsValueFromSpa(adps, spa, requireDet)
          elif spa in ADPS_B1_MAX:
            if int(base1) < ADPS_B1_MAX[spa]:
              adps = getAdpsValueFromSpa(adps, spa, requireDet)
          elif int(base1) >= 0:
            adps = getAdpsValueFromSpa(adps, spa, requireDet)
            
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

    if name not in byName:
      byName[name] = [info]
    else:
      byName[name].append(info)

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
