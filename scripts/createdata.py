# Used to generate the spell data included with the parser

import os.path

DBSpellsFile = 'spells_us.txt'
DBSpellsStrFile = 'spells_us_str.txt'

ADPS_CASTER_VALUE = 1
ADPS_MELEE_VALUE = 2
ADPS_TANK_VALUE = 4
ADPS_HEALER_VALUE = 8
ADPS_ALL_VALUE = ADPS_CASTER_VALUE + ADPS_MELEE_VALUE + ADPS_TANK_VALUE + ADPS_HEALER_VALUE
ADPS_CASTER = [ 8, 9, 15, 97, 118, 124, 127, 132, 170, 212, 273, 286, 294, 302, 303, 339, 351, 358, 375, 383, 399, 413, 461, 462, 501, 507 ]
ADPS_MELEE = [ 2, 4, 5, 11, 118, 119, 169, 171, 176, 177, 182, 184, 185, 186, 189, 190, 198, 200, 201, 211, 216, 220, 225, 227, 250, 252, 258, 266, 276, 279, 280, 301, 330, 339, 351, 364, 383, 418, 427, 429, 433, 459, 471, 473, 482, 496, 498, 499, 503 ]
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
UNIQUE_YOU = dict()
UNIQUE_OTHER = dict()
MAX_HITS = dict()

# bard SoR
ADPS_EXT_DUR[4516] = 1       # Improved Deftdance Disc
ADPS_EXT_DUR[8030] = 2       # Improved Thousand Blades Disc
# beast SoR
ADPS_EXT_DUR[4671] = 1       # Improved Protective Spirit Disc
# berserker SoR
ADPS_EXT_DUR[100160210] = 90 # Extended Havoc 
ADPS_EXT_DUR[100160170] = 5  # Improved Berserking Disc
ADPS_EXT_DUR[100160110] = 2  # Improved Cleaving Acrimony Disc
#ranger SoR
ADPS_EXT_DUR[100040210] = 20 # Improved Trueshot Disc
# rogue SoR
ADPS_EXT_DUR[100090250] = 8  # Extended Aspbleeder Disc
ADPS_EXT_DUR[100090240] = 15 # Improved Fatal Aim Disc
ADPS_EXT_DUR[6197] = 2       # Improved Frenzied Stabbing Disc
ADPS_EXT_DUR[100090260] = 90 # Improved Thief's Eyes
ADPS_EXT_DUR[4695] = 5       # Improved Twisted Chance Disc
# monk SoR
ADPS_EXT_DUR[100070090] = 3  # Improved Crystalpalm Discipline 
ADPS_EXT_DUR[100070160] = 3  # Improved Heel of Kanji Disc
ADPS_EXT_DUR[100070340] = 5  # Extended Impenetrable Disc
ADPS_EXT_DUR[100070200] = 5  # Extended Impenetrable Disc
ADPS_EXT_DUR[100070140] = 2  # Improved Scaledfist Disc
ADPS_EXT_DUR[4691] = 3       # Improved Speed Focus Disc
# sk/paladin SoR
ADPS_EXT_DUR[100030230] = 5   # Enduring Reproval
ADPS_EXT_DUR[100050180] = 20  # Extended Decrepit Skin
ADPS_EXT_DUR[100030240] = 25  # Extended Steely Stance
ADPS_EXT_DUR[100030180] = 18  # Extended Preservation of Marr
# war SoR
ADPS_EXT_DUR[100010190] = 108 # Extended Bracing Defense
ADPS_EXT_DUR[8000] = 90       # Extended Commanding Voice
ADPS_EXT_DUR[100010180] = 114 # Extended Field Armorer
ADPS_EXT_DUR[100010120] = 1   # Extended Shield Reflect

# sk/paladin SoR
MAX_HITS[100030230] = 9       # Enduring Reproval
MAX_HITS[100050180] = 80      # Extended Decrepit Skin
MAX_HITS[100030180] = 70      # Extended Preservation of Marr

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

      if landOnYou:
        if landOnYou not in UNIQUE_YOU:
          UNIQUE_YOU[landOnYou] = True
        else:
          UNIQUE_YOU[landOnYou] = False

      if landOnOther:
        if landOnOther not in UNIQUE_OTHER:
          UNIQUE_OTHER[landOnOther] = True
        else:
          UNIQUE_OTHER[landOnOther] = False
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
    maxTargets = int(data[142])
    origDuration = maxDuration

    spellLine = 0
    if data[164].isdigit():
      spellLine = int(data[164])

    if isTargetRing(name):
      spellTarget = 45  

    #if spellTarget == 8 and maxTargets > 5:
    #  print("%s has targets %d" % (name, maxTargets))

    # add focus AAs for additional hits based on line
    if spellLine > 0 and spellLine in MAX_HITS:
      maxHits = maxHits + MAX_HITS[spellLine]

    # add focus AAs for additional hits based on spell ID
    if intId > 0 and intId in MAX_HITS:
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
          if int(float(base1)) > 0:
            damaging = -1
          else:
            damaging = 1
          if int(float(base1)) <= -50000000:
            damaging = 2 # BANE
            
        if spa in ADPS_LIST:
          if spa in ADPS_B1_MIN:
            if int(float(base1)) >= ADPS_B1_MIN[spa]:
              adps = getAdpsValueFromSpa(adps, spa, requireDet)
          elif spa in ADPS_B1_MAX:
            if int(float(base1)) < ADPS_B1_MAX[spa]:
              adps = getAdpsValueFromSpa(adps, spa, requireDet)
          elif int(float(base1)) >= 0:
            adps = getAdpsValueFromSpa(adps, spa, requireDet)
            
        if spa == 339 and int(float(base2)) > 0:
          spa339s[base2] = id
          procs.append(base2)
        elif spa == 340 and int(float(base2)) > 0:
          spa340s[base2] = id
          procs.append(base2)
        elif spa == 373 and int(float(base2)) > 0:
          spa373s[base1] = id 
          procs.append(base1)
        elif spa == 374 and int(float(base2)) > 0:
          spa374s[base2] = id 
          procs.append(base2)
        elif spa == 406 and int(float(base1)) > 0:
          spa406s[base1] = id
          procs.append(base1)
        elif spa == 411 and classMask == 0:
          classMask = (int(float(base1)) >> 1)
		  
    # apply 100% buff extension
    if beneficial != 0 and focusable == 0 and combatSkill == 0 and maxDuration > 1:
      maxDuration = maxDuration * 2

    # add focus AAs that extend duration based on line
    if spellLine > 0 and spellLine in ADPS_EXT_DUR:
      maxDuration = maxDuration + ADPS_EXT_DUR[spellLine]
      
    # add focus AAs that extend duration based on spell ID
    if intId > 0 and intId in ADPS_EXT_DUR:
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
    info['landsOnYouAmbiguity'] = False
    info['landsOnOtherAmbiguity'] = False

    if id in DBSTRINGS:
      info['landsOnYou'] = DBSTRINGS[id]['landsOnYou']
      info['landsOnOther'] = DBSTRINGS[id]['landsOnOther']
      info['wearOff'] = DBSTRINGS[id]['wearOff']
      info['landsOnYouAmbiguity'] = (info['landsOnYou'] in UNIQUE_YOU and UNIQUE_YOU[info['landsOnYou']] == False)
      info['landsOnOtherAmbiguity'] = (info['landsOnOther'] in UNIQUE_OTHER and UNIQUE_OTHER[info['landsOnOther']] == False)

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
          if proc in spells and spells[proc]['classMask'] == 0:
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
    data = '%s^%s^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%d^%s^%s^%s' % (final[key]['intId'], final[key]['name'], final[key]['level'], final[key]['maxDuration'], final[key]['beneficial'], final[key]['maxHits'], final[key]['spellTarget'], final[key]['classMask'], final[key]['damaging'], final[key]['combatSkill'], final[key]['resist'], final[key]['songWindow'], final[key]['adps'], final[key]['mgb'], final[key]['rank'], final[key]['landsOnYouAmbiguity'], final[key]['landsOnOtherAmbiguity'], final[key]['landsOnYou'], final[key]['landsOnOther'], final[key]['wearOff'])
    output.write(data)
    output.write('\n')
  output.write('900001^Glyph of Destruction I^254^20^1^0^6^65407^0^0^0^0^3^0^1^0^1^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
  output.write('900002^Glyph of Destruction II^254^20^1^0^6^65407^0^0^0^0^3^0^2^0^1^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
  output.write('900003^Glyph of Destruction III^254^20^1^0^6^65407^0^0^0^0^3^0^3^0^1^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
  output.write('900004^Glyph of Destruction IV^254^20^1^0^6^65407^0^0^0^0^3^0^4^0^1^^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
  output.write('900005^Glyph of Destruction V^254^20^1^0^6^65407^0^0^0^0^3^0^5^^0^1^ is infused for destruction.^Your Glyph of Destruction fades away.')
  output.write('\n')
