from datetime import datetime, timedelta
import time


def parse_date_from_line(line):
    try:
        # Assuming the date format is "[Thu Feb 15 23:10:29 2024]"
        return datetime.strptime(line[1:25], "%a %b %d %H:%M:%S %Y")
    except ValueError:
        return None


def simulate_real_time_processing(source_file, destination_file):
    with open(source_file, "r") as src:
        all_lines = src.readlines()

    if not all_lines:
        return

    first_date = parse_date_from_line(all_lines[0])
    last_date = parse_date_from_line(all_lines[-1])

    if first_date and last_date:
        start_time = first_date
        current_time = datetime.now()

        groups = []
        current_group = []

        for line in all_lines:
            line_date = parse_date_from_line(line)
            if line_date:
                if line_date == start_time or not current_group:
                    current_group.append(line)
                else:
                    # New group
                    groups.append((current_group, start_time))
                    current_group = [line]
                    start_time = line_date

        if current_group:
            groups.append((current_group, start_time))

        # with open(destination_file2, 'a') as dst2:
        for group, group_time in groups:
            wait_seconds = (group_time - first_date).total_seconds() - (
                datetime.now() - current_time
            ).total_seconds()
            if wait_seconds > 0:
                time.sleep(wait_seconds)

            with open(destination_file, "a") as dst:
                for line in group:
                    new_timestamp = datetime.now().strftime("[%a %b %d %H:%M:%S %Y]")
                    dst.write(f"{new_timestamp}{line[26:]}")
                    dst.flush()
                    # dst2.write(f"{new_timestamp}{line[26:]}")
                    # dst2.flush()
                dst.close()


simulate_real_time_processing(
    "r:/eqlog_Kizant_xegony-selected2.txt", "r:/eqlog_Kizant_xegony.txt"
)
