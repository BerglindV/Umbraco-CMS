export interface UmbSwatchDetails {
	label: string;
	value: string;
}
export interface UmbServertimeOffset {
	/**
	 * offset in minutes relative to UTC
	 */
	offset: number;
}

export interface UmbNumberRangeValueType {
	min?: number;
	max?: number;
}

// TODO: this needs to use the UmbEntityUnique so we ensure that unique can be null
export interface UmbReferenceByUnique {
	unique: string;
}

export interface UmbReferenceByAlias {
	alias: string;
}

export interface UmbReferenceByUniqueAndType {
	type: string;
	unique: string;
}

export interface UmbUniqueItemModel {
	unique: string;
	name: string;
	icon?: string;
}

export interface UmbDateTimeWithTimeZone {
	date: string | undefined;
	timeZone: string | undefined;
}

export interface UmbTimeZonePickerValue {
	mode: 'all' | 'local' | 'custom';
	timeZones: Array<string>;
}
